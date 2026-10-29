using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Applications and eligibility (M3a, D46): a family drafts an application against a listing's
/// form, the listing's rules judge it, and submitting it once shares what the family chose.
/// </summary>
/// <remarks>
/// Families are staff accounts 9 and 10. The provider is the admin of a builtin-registration
/// listing's organisation that no other staff-based test uses; its admin also appears in an
/// unrelated class, which touches neither forms nor applications.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class ApplicationApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private IEnumerable<(string Email, Guid Org)> AllStaff() =>
        api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal)
            .Select(u => (u.Email, u.Memberships.Single().OrgId));

    private (string Email, Guid Org) Staff(int index) => AllStaff().ElementAt(index);

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    /// <summary>A published listing taking registrations here, at an organisation no staff-based test touches.</summary>
    private (Guid Id, string Slug, Guid Org) Listing(string mode = "builtin")
    {
        HashSet<Guid> taken = [.. AllStaff().Take(11).Select(s => s.Org)];
        var listing = api.Data.Listings.First(l => l.Status == "published" && l.RegistrationMode == mode && !taken.Contains(l.OrgId)
            && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved");
        return (listing.Id, listing.Slug, listing.OrgId);
    }

    private Guid[] Neighbourhoods() => [.. api.Data.Areas.Where(a => a.Kind == "neighbourhood").OrderBy(a => a.Code, StringComparer.Ordinal).Select(a => a.Id)];

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        JsonElement problem = await JsonAsync(response, status);
        problem.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid listing, Guid member, string? key)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> AnswerAsync(HttpClient client, Guid application, object answers) =>
        client.PatchAsJsonAsync($"api/v1/applications/{application}", new { answers }, Ct);

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid application, params string[] fields) =>
        client.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = fields }, Ct);

    [Fact]
    public async Task A_family_drafts_answers_and_submits_once_sharing_what_it_chose()
    {
        RequireDatabase();
        (Guid listing, _, Guid org) = Listing();
        Guid[] hoods = Neighbourhoods();
        using HttpClient provider = await SignedInAsync(AdminOf(org));
        using HttpClient family = await SignedInAsync(Staff(9).Email);
        using HttpClient stranger = await SignedInAsync(Staff(10).Email);

        // The provider's form and rules: ages 8 to 14 and one neighbourhood, required; grades 3 to 8, advisory.
        Guid form = (await JsonAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/forms", new
        {
            name = "Intake " + Guid.NewGuid().ToString("N")[..6],
            questions = new object[]
            {
                new { key = "school_name", kind = "short_text", label = new Dictionary<string, string> { ["en"] = "School" }, required = true },
                new { key = "household_income", kind = "number", label = new Dictionary<string, string> { ["en"] = "Household income" }, sensitive = true, min = 0 },
                new { key = "photo_release", kind = "consent", label = new Dictionary<string, string> { ["en"] = "Photos are fine." }, required = true },
            },
        }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/forms/{form}/publish", UriKind.Relative), null, Ct));
        (await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/form", new { formId = form }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        object[] rules =
        [
            new { kind = "age_at_start", config = new { min = 8, max = 14 }, isHard = true },
            new { kind = "grade_range", config = new { min = 3, max = 8 }, isHard = false },
            new { kind = "neighbourhood_in", config = new { neighbourhoodIds = new[] { hoods[0] } }, isHard = true },
        ];
        (await JsonAsync(await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility", new { rules }, Ct))).GetArrayLength().ShouldBe(3);

        // The dry run: too old fails, a fit passes, an unknown neighbourhood can't be decided.
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string tenYearsAgo = DateTime.UtcNow.AddYears(-10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string twentyYearsAgo = DateTime.UtcNow.AddYears(-20).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        async Task<JsonValueKind> PreviewAsync(object who) =>
            (await JsonAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility/preview", who, Ct))).GetProperty("isEligible").ValueKind;
        (await PreviewAsync(new { dateOfBirth = twentyYearsAgo, neighbourhoodId = hoods[0] })).ShouldBe(JsonValueKind.False);
        (await PreviewAsync(new { dateOfBirth = tenYearsAgo, grade = 1, neighbourhoodId = hoods[0] })).ShouldBe(JsonValueKind.True);
        (await PreviewAsync(new { dateOfBirth = tenYearsAgo })).ShouldBe(JsonValueKind.Null);
        (await PreviewAsync(new { dateOfBirth = today, neighbourhoodId = hoods[0], rules = new[] { new { kind = "age_at_start", config = new { max = 1 }, isHard = true } } })).ShouldBe(JsonValueKind.True);

        // The family: one child who fits, one too old.
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 3 }, Ct));
        Guid kemi = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Kemi", lastName = "Adeyemi", dateOfBirth = tenYearsAgo, grade = 5, neighbourhoodId = hoods[0] }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Guid tobi = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Tobi", lastName = "Adeyemi", dateOfBirth = twentyYearsAgo, neighbourhoodId = hoods[0] }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        await ProblemAsync(await CreateAsync(family, listing, kemi, key: null), HttpStatusCode.BadRequest, "application.idempotency_key");
        string key = "kemi-" + Guid.NewGuid().ToString("N");
        JsonElement draft = await JsonAsync(await CreateAsync(family, listing, kemi, key), HttpStatusCode.Created);
        Guid application = draft.GetProperty("id").GetGuid();
        draft.GetProperty("formId").GetGuid().ShouldBe(form);
        draft.GetProperty("eligibility").GetProperty("isEligible").GetBoolean().ShouldBeTrue();
        (await JsonAsync(await CreateAsync(family, listing, kemi, key))).GetProperty("id").GetGuid().ShouldBe(application);
        await ProblemAsync(await CreateAsync(family, listing, kemi, "another-" + Guid.NewGuid().ToString("N")), HttpStatusCode.Conflict, "application.exists");

        // The questions the draft answers, as the family is shown them (D72); nobody else's.
        JsonElement questions = await JsonAsync(await family.GetAsync(new Uri($"api/v1/applications/{application}/form", UriKind.Relative), Ct));
        questions.GetProperty("formId").GetGuid().ShouldBe(form);
        questions.GetProperty("questions").EnumerateArray().Select(q => (q.GetProperty("key").GetString(), q.GetProperty("label").GetString()))
            .ShouldBe([("school_name", "School"), ("household_income", "Household income"), ("photo_release", "Photos are fine.")]);
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/applications/{application}/form", UriKind.Relative), Ct), HttpStatusCode.NotFound, "application.not_found");

        // Answers: checked against the form, saved a few at a time, the sensitive one sealed.
        JsonElement saved = await JsonAsync(await AnswerAsync(family, application, new Dictionary<string, object> { ["school_name"] = "PS 12", ["household_income"] = 42137 }));
        saved.GetProperty("answers").GetProperty("household_income").GetInt32().ShouldBe(42137);
        string snapshot = await api.ScalarAsync<string>("select answers_snapshot::text from enrollment.application where id = @a", ("a", application));
        snapshot.ShouldNotContain("42137");
        snapshot.ShouldContain("sealed");
        await ProblemAsync(await AnswerAsync(family, application, new Dictionary<string, object> { ["school_name"] = 5 }), HttpStatusCode.BadRequest, "application.answer");
        await ProblemAsync(await AnswerAsync(family, application, new Dictionary<string, object> { ["favourite_colour"] = "teal" }), HttpStatusCode.BadRequest, "application.answer_key");
        await ProblemAsync(await SubmitAsync(family, application, "member_names"), HttpStatusCode.BadRequest, "application.answer_missing");
        await JsonAsync(await AnswerAsync(family, application, new Dictionary<string, object> { ["photo_release"] = true }));

        // Submitted once, sharing at least the name; the same again changes nothing.
        await ProblemAsync(await SubmitAsync(family, application, "member_birth_dates"), HttpStatusCode.BadRequest, "application.share");
        JsonElement submitted = await JsonAsync(await SubmitAsync(family, application, "member_names", "member_birth_dates"));
        submitted.GetProperty("status").GetString().ShouldBe("submitted");
        submitted.GetProperty("eligibility").GetProperty("isEligible").GetBoolean().ShouldBeTrue();
        (await JsonAsync(await SubmitAsync(family, application, "member_names"))).GetProperty("submittedAt").GetString()
            .ShouldBe(submitted.GetProperty("submittedAt").GetString());
        await ProblemAsync(await AnswerAsync(family, application, new Dictionary<string, object> { ["school_name"] = "PS 13" }), HttpStatusCode.Conflict, "application.not_draft");

        (await api.ScalarAsync<string>("select value_text from enrollment.application_answer where application_id = @a and question_key = 'school_name'", ("a", application))).ShouldBe("PS 12");
        (await api.ScalarAsync<bool>("select is_sensitive and value_number is null from enrollment.application_answer where application_id = @a and question_key = 'household_income'", ("a", application))).ShouldBeTrue();

        // The organisation now holds a share of Kemi, for this listing, with her name.
        JsonElement inbox = await provider.GetFromJsonAsync<JsonElement>($"api/v1/org/{org}/family-shares", Ct);
        JsonElement share = inbox.EnumerateArray().Single(s => s.GetProperty("listingId").GetGuid() == listing);
        share.GetProperty("members")[0].GetProperty("firstName").GetString().ShouldBe("Kemi");

        // Tobi is too old: complete, but refused at submission.
        Guid tobisApplication = (await JsonAsync(await CreateAsync(family, listing, tobi, "tobi-" + Guid.NewGuid().ToString("N")), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await AnswerAsync(family, tobisApplication, new Dictionary<string, object> { ["school_name"] = "Kettle High", ["photo_release"] = true }));
        HttpResponseMessage ineligible = await SubmitAsync(family, tobisApplication, "member_names");
        JsonElement refusal = await JsonAsync(ineligible, HttpStatusCode.Conflict);
        refusal.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:application.ineligible");
        refusal.GetProperty("rules").GetString().ShouldBe("age_at_start");

        // Nobody else sees Kemi's application.
        (await stranger.GetAsync(new Uri($"api/v1/applications/{application}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetFromJsonAsync<JsonElement>("api/v1/applications", Ct)).GetArrayLength().ShouldBe(0);

        // Kemi's is withdrawn; Tobi's draft, never sent, is thrown away.
        JsonElement mine = await family.GetFromJsonAsync<JsonElement>("api/v1/applications", Ct);
        mine.GetArrayLength().ShouldBe(2);
        (await family.PostAsync(new Uri($"api/v1/applications/{application}/withdraw", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ProblemAsync(await family.PostAsync(new Uri($"api/v1/applications/{application}/withdraw", UriKind.Relative), null, Ct), HttpStatusCode.Conflict, "application.not_withdrawable");
        (await family.PostAsync(new Uri($"api/v1/applications/{tobisApplication}/withdraw", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await family.GetAsync(new Uri($"api/v1/applications/{tobisApplication}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await family.GetFromJsonAsync<JsonElement>($"api/v1/applications/{application}", Ct)).GetProperty("status").GetString().ShouldBe("withdrawn");

        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.application.changed' and subject_ids = array[@a] and detail->>'change' = 'submitted'",
            ("a", application))).ShouldBe(1);
    }

    [Fact]
    public async Task Rules_are_checked_shown_to_families_and_kept_to_the_organisations_admins()
    {
        RequireDatabase();
        (Guid listing, string slug, Guid org) = Listing();
        (Guid external, _, _) = Listing("external");
        Guid[] hoods = Neighbourhoods();
        using HttpClient provider = await SignedInAsync(AdminOf(org));
        using HttpClient family = await SignedInAsync(Staff(10).Email);

        async Task RefusedAsync(object rule, string code)
        {
            HttpResponseMessage refused = await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility", new { rules = new[] { rule } }, Ct);
            JsonElement problem = await JsonAsync(refused, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);
            problem.GetProperty("rule").GetInt32().ShouldBe(1);
        }

        await RefusedAsync(new { kind = "district_resident", config = new { } }, "eligibility.kind");
        await RefusedAsync(new { kind = "age_at_start", config = new { } }, "eligibility.config");
        await RefusedAsync(new { kind = "age_at_start", config = new { min = 9, max = 6 } }, "eligibility.config");
        await RefusedAsync(new { kind = "grade_range", config = new { min = 3, neighbourhoodIds = new[] { hoods[0] } } }, "eligibility.config");
        await RefusedAsync(new { kind = "neighbourhood_in", config = new { neighbourhoodIds = new[] { Guid.NewGuid() } } }, "eligibility.config");

        await JsonAsync(await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility",
            new { rules = new object[] { new { kind = "grade_range", config = new { min = 6, max = 8 }, isHard = true } } }, Ct));
        using HttpClient anyone = api.Client();
        JsonElement shown = await anyone.GetFromJsonAsync<JsonElement>($"api/v1/public/listings/{slug}/eligibility", Ct);
        JsonElement rule = shown.EnumerateArray().Single();
        rule.GetProperty("kind").GetString().ShouldBe("grade_range");
        rule.GetProperty("config").GetProperty("min").GetInt32().ShouldBe(6);

        // The family's side: a neighbourhood must be the county's, and an external listing takes no applications here.
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        await ProblemAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Ife", lastName = "Bello", dateOfBirth = "2014-04-04", neighbourhoodId = Guid.NewGuid() }, Ct), HttpStatusCode.BadRequest, "family.member_neighbourhood");
        Guid ife = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Ife", lastName = "Bello", dateOfBirth = "2014-04-04", neighbourhoodId = hoods[1] }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await ProblemAsync(await CreateAsync(family, external, ife, "ife-" + Guid.NewGuid().ToString("N")), HttpStatusCode.BadRequest, "application.registration");

        // A family member who can't read the organisation's rules.
        (await family.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/eligibility", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
