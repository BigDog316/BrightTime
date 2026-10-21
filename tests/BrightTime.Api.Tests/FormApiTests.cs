using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Registration forms (M3a, D45): an organisation's admins draft, publish, version and retire
/// them and attach them to listings; a family sees the published form in its own language.
/// </summary>
/// <remarks>
/// Signs in as staff accounts 7 and 8 and as the admin of staff 7's organisation. Every admin is
/// already used by another class, but none of those touches forms, and the collection runs
/// one class at a time.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class FormApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (string Email, Guid Org) Staff(int index)
    {
        var staff = api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal).ElementAt(index);
        return (staff.Email, staff.Memberships.Single().OrgId);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static object[] Questions(bool withShoeSize = false)
    {
        List<object> questions =
        [
            new { key = "school_name", kind = "short_text", label = new Dictionary<string, string> { ["en"] = "Which school do they go to?", ["es"] = "¿A qué escuela va?" }, required = true, maxLength = 120 },
            new
            {
                key = "tshirt",
                kind = "single_choice",
                label = new Dictionary<string, string> { ["en"] = "T-shirt size" },
                choices = new[]
                {
                    new { key = "youth_s", label = new Dictionary<string, string> { ["en"] = "Youth S", ["es"] = "Juvenil S" } },
                    new { key = "youth_m", label = new Dictionary<string, string> { ["en"] = "Youth M" } },
                },
            },
            new { key = "photo_release", kind = "consent", label = new Dictionary<string, string> { ["en"] = "You may take photos of them at the program." }, required = true },
            new { key = "household_income", kind = "number", label = new Dictionary<string, string> { ["en"] = "Yearly household income" }, sensitive = true, min = 0 },
        ];
        if (withShoeSize)
        {
            questions.Add(new { key = "shoe_size", kind = "number", label = new Dictionary<string, string> { ["en"] = "Shoe size" }, min = 1, max = 15 });
        }

        return [.. questions];
    }

    [Fact]
    public async Task A_form_is_drafted_published_versioned_and_shown_to_families_in_their_language()
    {
        RequireDatabase();
        (_, Guid org) = Staff(7);
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        var listing = api.Data.Listings.First(l => l.OrgId == org && l.Status == "published");

        JsonElement draft = await OkAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/forms", new { name = "Summer intake", questions = Questions() }, Ct), HttpStatusCode.Created);
        Guid v1 = draft.GetProperty("id").GetGuid();
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("version").GetInt32().ShouldBe(1);
        draft.GetProperty("hash").GetString()!.Length.ShouldBe(64);

        JsonElement edited = await OkAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/forms/{v1}", new { questions = Questions(withShoeSize: true) }, Ct));
        edited.GetProperty("questions").GetArrayLength().ShouldBe(5);
        edited.GetProperty("hash").GetString().ShouldNotBe(draft.GetProperty("hash").GetString());

        HttpResponseMessage early = await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing.Id}/form", new { formId = v1 }, Ct);
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(early)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.not_published");

        (await OkAsync(await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{v1}/publish", UriKind.Relative), null, Ct))).GetProperty("status").GetString().ShouldBe("published");
        HttpResponseMessage fixedNow = await admin.PutAsJsonAsync($"api/v1/org/{org}/forms/{v1}", new { questions = Questions() }, Ct);
        fixedNow.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(fixedNow)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.not_draft");

        (await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing.Id}/form", new { formId = v1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A family reads it in Spanish where the provider wrote Spanish, English elsewhere.
        using HttpClient family = api.Client();
        family.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es");
        JsonElement shown = await family.GetFromJsonAsync<JsonElement>($"api/v1/public/listings/{listing.Slug}/form", Ct);
        shown.GetProperty("version").GetInt32().ShouldBe(1);
        JsonElement[] asked = [.. shown.GetProperty("questions").EnumerateArray()];
        asked[0].GetProperty("label").GetString().ShouldBe("¿A qué escuela va?");
        asked[1].GetProperty("label").GetString().ShouldBe("T-shirt size");
        asked[1].GetProperty("choices")[0].GetProperty("label").GetString().ShouldBe("Juvenil S");
        asked[2].GetProperty("required").GetBoolean().ShouldBeTrue();
        asked[3].GetProperty("sensitive").GetBoolean().ShouldBeTrue();

        // In use, it can't be retired; a new version comes as a draft, one at a time.
        HttpResponseMessage inUse = await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{v1}/retire", UriKind.Relative), null, Ct);
        inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(inUse)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.in_use");
        JsonElement next = await OkAsync(await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{v1}/revise", UriKind.Relative), null, Ct), HttpStatusCode.Created);
        Guid v2 = next.GetProperty("id").GetGuid();
        next.GetProperty("version").GetInt32().ShouldBe(2);
        next.GetProperty("status").GetString().ShouldBe("draft");
        next.GetProperty("hash").GetString().ShouldBe(edited.GetProperty("hash").GetString());
        HttpResponseMessage twice = await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{v1}/revise", UriKind.Relative), null, Ct);
        (await ProblemAsync(twice)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.draft_exists");

        await OkAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/forms/{v2}", new { questions = Questions() }, Ct));
        await OkAsync(await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{v2}/publish", UriKind.Relative), null, Ct));
        (await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing.Id}/form", new { formId = v2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await OkAsync(await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{v1}/retire", UriKind.Relative), null, Ct))).GetProperty("status").GetString().ShouldBe("retired");

        (await family.GetFromJsonAsync<JsonElement>($"api/v1/public/listings/{listing.Slug}/form", Ct)).GetProperty("version").GetInt32().ShouldBe(2);
        JsonElement mine = await admin.GetFromJsonAsync<JsonElement>($"api/v1/org/{org}/forms", Ct);
        mine.EnumerateArray().Select(f => (f.GetProperty("version").GetInt32(), f.GetProperty("status").GetString())).ShouldBe([(2, "published"), (1, "retired")]);

        // Taken off the listing, the family sees no form.
        (await admin.DeleteAsync(new Uri($"api/v1/org/{org}/listings/{listing.Id}/form", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await family.GetAsync(new Uri($"api/v1/public/listings/{listing.Slug}/form", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bad_forms_and_people_outside_the_organisation_are_refused()
    {
        RequireDatabase();
        (string staffEmail, Guid org) = Staff(8);
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffEmail);

        // Staff run programs; owners and admins write the forms.
        HttpResponseMessage notAdmin = await staff.GetAsync(new Uri($"api/v1/org/{org}/forms", UriKind.Relative), Ct);
        notAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemAsync(notAdmin)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.forbidden");

        async Task RefusedAsync(object[] questions, string code, int? question)
        {
            HttpResponseMessage refused = await admin.PostAsJsonAsync($"api/v1/org/{org}/forms", new { name = "Bad " + Guid.NewGuid().ToString("N")[..8], questions }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, code);
            JsonElement problem = await ProblemAsync(refused);
            problem.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);
            if (question is { } at)
            {
                problem.GetProperty("question").GetInt32().ShouldBe(at);
            }
        }

        Dictionary<string, string> en = new() { ["en"] = "A question" };
        await RefusedAsync([], "form.questions", null);
        await RefusedAsync([new { key = "a", kind = "yes_no", label = en }, new { key = "a", kind = "yes_no", label = en }], "form.question_key", 2);
        await RefusedAsync([new { key = "Bad Key", kind = "yes_no", label = en }], "form.question_key", 1);
        await RefusedAsync([new { key = "a", kind = "signature_pad", label = en }], "form.question_kind", 1);
        await RefusedAsync([new { key = "a", kind = "yes_no", label = new Dictionary<string, string> { ["es"] = "Solo español" } }], "form.question_label", 1);
        await RefusedAsync([new { key = "a", kind = "yes_no", label = new Dictionary<string, string> { ["en"] = "Hi", ["klingon"] = "nuqneH" } }], "form.question_label", 1);
        await RefusedAsync([new { key = "a", kind = "short_text", label = en, choices = new[] { new { key = "x", label = en }, new { key = "y", label = en } } }], "form.question_choices", 1);
        await RefusedAsync([new { key = "a", kind = "single_choice", label = en, choices = new[] { new { key = "x", label = en } } }], "form.question_choices", 1);
        await RefusedAsync([new { key = "a", kind = "consent", label = en, required = false }], "form.question_limits", 1);
        await RefusedAsync([new { key = "a", kind = "number", label = en, min = 5, max = 1 }], "form.question_limits", 1);
        await RefusedAsync([new { key = "a", kind = "date", label = en, maxLength = 10 }], "form.question_limits", 1);

        JsonElement made = await OkAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/forms", new { name = "Fall intake", questions = new[] { new { key = "a", kind = "yes_no", label = en } } }, Ct), HttpStatusCode.Created);
        HttpResponseMessage taken = await admin.PostAsJsonAsync($"api/v1/org/{org}/forms", new { name = "Fall intake", questions = new[] { new { key = "a", kind = "yes_no", label = en } } }, Ct);
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(taken)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.name_taken");

        // Another organisation's listing, or another organisation's form, is nothing to this admin.
        Guid formId = made.GetProperty("id").GetGuid();
        await OkAsync(await admin.PostAsync(new Uri($"api/v1/org/{org}/forms/{formId}/publish", UriKind.Relative), null, Ct));
        Guid theirListing = api.Data.Listings.First(l => l.OrgId != org).Id;
        HttpResponseMessage notTheirs = await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{theirListing}/form", new { formId }, Ct);
        notTheirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(notTheirs)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:form.listing_not_found");

        (_, Guid otherOrg) = Staff(7);
        using HttpClient otherAdmin = await SignedInAsync(AdminOf(otherOrg));
        (await otherAdmin.GetAsync(new Uri($"api/v1/org/{otherOrg}/forms/{formId}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.GetAsync(new Uri($"api/v1/org/{org}/forms/{formId}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
