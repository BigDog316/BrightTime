using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Modules.Enrollment.Forms;
using BrightTime.Seed;
using BrightTime.Web.Components.Console.Forms;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's form builder (D62): a form started with its first question, questions
/// added, edited, moved and translated a form at a time, published, attached to a listing, revised
/// into a new draft, and kept from retiring while in use.
/// </summary>
/// <remarks>
/// The provider is the fourth approved organisation from sorted index 52 on with a published
/// builtin listing (the applications, attendance and payments tests take the first three); the
/// stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed partial class FormPagesTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    [Theory]
    [InlineData("Does your child have any allergies?", "does_your_child_have_any_allergies")]
    [InlineData("T-shirt size", "t_shirt_size")]
    [InlineData("Écoles fréquentées", "ecoles_frequentees")]
    [InlineData("2024 programme", "q")]
    [InlineData("শার্টের মাপ", "q")]
    public void A_key_comes_from_the_english_words(string words, string key) =>
        QuestionInput.Key(words, [], "q").ShouldBe(key);

    [Fact]
    public void A_taken_key_gets_a_number_and_a_long_one_is_cut()
    {
        QuestionInput.Key("Size", ["size", "size_2"], "q").ShouldBe("size_3");
        QuestionInput.Key(new string('a', 60), [], "q").Length.ShouldBeLessThanOrEqualTo(40);
    }

    [Fact]
    public void An_edited_choice_list_keeps_the_keys_of_choices_it_still_has()
    {
        IReadOnlyList<FormChoice> before = QuestionInput.Choices("Small\nMedium\nLarge", null);
        before.Select(c => c.Key).ShouldBe(["small", "medium", "large"]);
        IReadOnlyList<FormChoice> after = QuestionInput.Choices("Medium\nLarge\nExtra large\n\n", before);
        after.Select(c => c.Key).ShouldBe(["medium", "large", "extra_large"]);
    }

    [Fact]
    public async Task Admins_build_publish_and_attach_a_form_and_nobody_else_can()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(3).First();
        Guid listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id;
        string forms = $"/provider/orgs/{provider.Id}/forms";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(AdminOf(provider));
        (await admin.GetAsync(forms)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A form, started with its first question.
        HttpResponseMessage created = await admin.PostAsync(forms, "create", ("name", "Summer registration"),
            ("label", "Does your child have any allergies?"), ("help", ""), ("kind", "yes_no"), ("choices", ""), ("maxLength", ""), ("min", ""), ("max", ""), ("required", "true"));
        created.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string editor = created.Headers.Location!.AbsolutePath;
        await admin.GetAsync(created.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("Form created as a draft.");
        Guid form = Guid.Parse(editor[(editor.LastIndexOf('/') + 1)..]);

        // A choice question, a refused one, then an edit that keeps its choices' keys and adds Spanish.
        (await admin.PostAsync(editor, "add", ("label", "T-shirt size"), ("help", ""), ("kind", "single_choice"), ("choices", "Small\nMedium\nLarge"),
            ("maxLength", ""), ("min", ""), ("max", ""))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "add", ("label", "Favourite day"), ("help", ""), ("kind", "single_choice"), ("choices", "Monday"),
            ("maxLength", ""), ("min", ""), ("max", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Question 3: give 2 to 30 choices");
        (await admin.PostAsync(editor, "edit-t_shirt_size", ("question", "t_shirt_size"), ("label", "T-shirt size"), ("help", "For the team shirt"), ("kind", "single_choice"),
            ("choices", "Small\nMedium\nLarge\nExtra large"), ("maxLength", ""), ("min", ""), ("max", ""), ("label-es", "Talla de camiseta"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string schema = await db.ScalarAsync<string>("select schema_json::text from enrollment.form_definition where id = @f", ("f", form));
        foreach (string expected in new[] { "\"key\": \"does_your_child_have_any_allergies\"", "\"key\": \"t_shirt_size\"", "\"key\": \"small\"", "\"key\": \"extra_large\"", "Talla de camiseta" })
        {
            schema.ShouldContain(expected);
        }

        // Moved to the top, then published and given to a listing.
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "up-t_shirt_size", ("question", "t_shirt_size"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<string>("select schema_json->'questions'->0->>'key' from enrollment.form_definition where id = @f", ("f", form))).ShouldBe("t_shirt_size");
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "publish")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(editor);
        admin.Html.ShouldContain("This version is published and can&#x27;t change.");
        admin.Html.ShouldNotContain("name=\"_handler\" value=\"add\"");
        (await admin.PostAsync(editor, "attach", ("listing", listing.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<Guid>("select form_definition_id from enrollment.listing_form where listing_id = @l", ("l", listing))).ShouldBe(form);

        // In use, it can't be retired; its next version starts as a draft with the same questions.
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "retire")).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("A listing still uses this form.");
        HttpResponseMessage revised = await admin.PostAsync(editor, "revise");
        revised.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(revised.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("This is the next version, as a draft.");
        admin.Html.ShouldContain("Version 2");
        QuestionLabels().Count(admin.Html).ShouldBeGreaterThanOrEqualTo(2);

        // Another organisation's admin sees none of it.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(forms)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.GetAsync(editor)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("T-shirt size");
    }

    [GeneratedRegex("<strong dir=\"auto\">")]
    private static partial Regex QuestionLabels();
}
