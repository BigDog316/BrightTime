using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Seed;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's eligibility rules and waivers (D63): rules added, refused and removed,
/// tried against a made-up applicant; a waiver written, published, asked for on a listing, kept
/// from retiring while in use and revised; and only owners and admins allowed.
/// </summary>
/// <remarks>
/// The provider is the fifth approved organisation from sorted index 52 on with a published
/// builtin listing (the applications, attendance, payments and forms tests take the first four);
/// the stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed partial class EligibilityWaiverPagesTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private (SeedOrganisation Provider, Guid Listing, SeedOrganisation Stranger) Arrange()
    {
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(4).First();
        return (provider, db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id, sorted[50]);
    }

    [Fact]
    public async Task Admins_set_and_try_an_activitys_eligibility_rules()
    {
        RequireDatabase();
        (SeedOrganisation provider, Guid listing, SeedOrganisation stranger) = Arrange();
        string page = $"/provider/orgs/{provider.Id}/listings/{listing}/eligibility";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(AdminOf(provider));
        (await admin.GetAsync($"/provider/orgs/{provider.Id}/forms")).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain($"href=\"{page}\"");
        (await admin.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("No rules: anyone can apply.");
        string neighbourhood = NeighbourhoodChoice().Match(admin.Html).Groups[1].Value;

        // An age rule that must be met, and an advisory neighbourhood rule; a backwards range is refused.
        (await admin.PostAsync(page, "add", ("kind", "age_at_start"), ("min", "9"), ("max", "12"), ("on", "2027-07-01"), ("hard", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(page);
        (await admin.PostAsync(page, "add", ("kind", "neighbourhood_in"), ("min", ""), ("max", ""), ("on", ""), ("neighbourhood", neighbourhood))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(page);
        admin.Html.ShouldContain("Age at the start: 9 to 12");
        admin.Html.ShouldContain("(advice only)");
        (await admin.PostAsync(page, "add", ("kind", "grade_range"), ("min", "8"), ("max", "3"), ("on", ""), ("hard", "true"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Rule 3:");
        (await db.ScalarAsync<long>("select count(*) from enrollment.eligibility_rule where listing_id = @l", ("l", listing))).ShouldBe(2);

        // Tried against made-up applicants: eleven on the day fits, sixteen doesn't; nothing is saved.
        (await admin.PostAsync(page, "try", ("dob", "2016-01-01"), ("grade", ""), ("neighbourhood", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Eligible: Yes");
        (await admin.PostAsync(page, "try", ("dob", "2011-01-01"), ("grade", ""), ("neighbourhood", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Eligible: No");

        // A rule removed.
        Guid rule = await db.ScalarAsync<Guid>("select id from enrollment.eligibility_rule where listing_id = @l and rule_kind = 'neighbourhood_in'", ("l", listing));
        (await admin.PostAsync(page, $"remove-{rule:N}", ("rule", rule.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<long>("select count(*) from enrollment.eligibility_rule where listing_id = @l", ("l", listing))).ShouldBe(1);

        using Browser other = db.NewBrowser();
        await other.SignInAsync(AdminOf(stranger));
        (await other.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admins_write_publish_and_ask_for_a_waiver_and_nobody_else_can()
    {
        RequireDatabase();
        (SeedOrganisation provider, Guid listing, SeedOrganisation stranger) = Arrange();
        string waivers = $"/provider/orgs/{provider.Id}/waivers";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(AdminOf(provider));
        (await admin.GetAsync(waivers)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Written as a draft, then changed.
        HttpResponseMessage created = await admin.PostAsync(waivers, "create", ("title", "Photo release"), ("body", "We may take photos."), ("guardian", "true"));
        created.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string editor = created.Headers.Location!.AbsolutePath;
        Guid waiver = Guid.Parse(editor[(editor.LastIndexOf('/') + 1)..]);
        await admin.GetAsync(created.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("Waiver created as a draft.");
        (await admin.PostAsync(editor, "text", ("body", "We may take **photos** at sessions."), ("guardian", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Published, its exact text shown and locked, then asked for on a listing.
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "publish")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(editor);
        admin.Html.ShouldContain("<strong>photos</strong>");
        admin.Html.ShouldContain("families sign exactly this text");
        (await admin.PostAsync(editor, "attach", ("listing", listing.ToString()), ("required", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<bool>("select is_required from enrollment.listing_waiver where listing_id = @l and waiver_id = @w", ("l", listing), ("w", waiver))).ShouldBeTrue();

        // In use, it can't be retired; its next version starts as a draft.
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "retire")).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("A listing still asks families to sign this.");
        HttpResponseMessage revised = await admin.PostAsync(editor, "revise");
        revised.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(revised.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("This is the next version, as a draft.");
        admin.Html.ShouldContain("Version 2");

        using Browser other = db.NewBrowser();
        await other.SignInAsync(AdminOf(stranger));
        (await other.GetAsync(waivers)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.GetAsync(editor)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        other.Html.ShouldNotContain("Photo release");
    }

    [GeneratedRegex("name=\"neighbourhood\" value=\"([0-9a-f-]{36})\"")]
    private static partial Regex NeighbourhoodChoice();
}
