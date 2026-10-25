using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Goals, skills and projects (M4, D69). A young person keeps private goals and lists their own
/// skills. An organisation they're enrolled with endorses another skill. They write a project about
/// its activity; staff send it back once, then approve it. Published, the project and both skills
/// appear on the shown portfolio, the goal never; changing the project takes it back down.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the thirteenth organisation with no staff (PortfolioExportApiTests
/// takes the twelfth); the ninth's admin is the outsider. Skill terms are added here, as an organiser
/// would in Taxonomy: the seed has none.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class LearnerRecordApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org) Programme(int nth)
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(nth).First();
        return unstaffed.First(l => l.OrgId == org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<(HttpClient Client, Guid User)> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        Guid user = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", email));
        return (ApiFixture.WithBearer(api.Client(), tokens.AccessToken), user);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private async Task<Guid> TermAsync(string vocabulary, string name)
    {
        Guid id = Guid.NewGuid();
        await api.ExecuteAsync("""
            insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values (@id, @t, @v, @c);
            insert into platform.taxonomy_term_name (term_id, locale, name) values (@id, 'en', @n);
            """, ("id", id), ("t", api.Data.TenantId), ("v", vocabulary), ("c", $"{name.ToLowerInvariant().Replace(' ', '-')}-{id:N}"), ("n", name));
        return id;
    }

    /// <summary>The young person's own household entry, linked to their account, applied, accepted and enrolled.</summary>
    private async Task<Guid> EnrolledAsync(HttpClient learner, Guid learnerId, HttpClient admin, Guid listing, Guid org)
    {
        await JsonAsync(await learner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 1 }, Ct));
        string dob = DateTime.UtcNow.AddYears(-19).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await learner.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Tobi", lastName = "Adeyemi", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        apply.Headers.Add("Idempotency-Key", "records-" + Guid.NewGuid().ToString("N"));
        Guid application = (await JsonAsync(await learner.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await learner.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
        await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
        await JsonAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = learnerId }, Ct));
        return await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
    }

    [Fact]
    public async Task Goals_stay_private_skills_are_listed_and_endorsed_and_a_project_is_shown_only_as_staff_approved_it()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme(12);
        (_, Guid otherOrg) = Programme(8);
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient outsider, _) = await SignedInAsync(AdminOf(otherOrg));
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anonymous = api.Client();
        using (admin)
        using (outsider)
        using (learner)
        {
            Guid teamwork = await TermAsync("skill", "Teamwork");
            Guid fieldwork = await TermAsync("skill", "Field research");
            Guid interest = await TermAsync("interest", "Rivers");
            Guid enrolment = await EnrolledAsync(learner, learnerId, admin, listing, org);
            string activity = api.Data.Listings.Single(l => l.Id == listing).Title;
            await api.ExecuteAsync("""
                insert into learning.participation_record (id, tenant_id, user_id, listing_id, org_id, title_snapshot, org_name_snapshot, started_on, sessions_attended, hours, source, created_at)
                values (gen_random_uuid(), @t, @u, @l, @o, @title, 'The organisation', current_date - 30, 6, 9, 'attendance', now())
                """, ("t", api.Data.TenantId), ("u", learnerId), ("l", listing), ("o", org), ("title", activity));

            // Goals: their own, about an interest or a skill, and private.
            await ProblemAsync(await learner.PostAsJsonAsync("api/v1/me/goals", new { title = "Lead a river walk", status = "someday" }, Ct), HttpStatusCode.BadRequest, "goals.status");
            JsonElement goal = await JsonAsync(await learner.PostAsJsonAsync("api/v1/me/goals",
                new { title = "Lead a river walk", detail = "For the younger group", targetTermId = interest, targetDate = "2027-06-01" }, Ct), HttpStatusCode.Created);
            (goal.GetProperty("status").GetString(), goal.GetProperty("target").GetString()).ShouldBe(("active", "Rivers"));
            JsonElement achieved = await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/goals/{goal.GetProperty("id").GetGuid()}",
                new { title = "Lead a river walk", status = "achieved" }, Ct));
            achieved.GetProperty("achievedAt").ValueKind.ShouldBe(JsonValueKind.String);

            // Skills: their own at a level, and one the organisation endorses through the enrolment.
            await ProblemAsync(await learner.PostAsJsonAsync("api/v1/me/skills", new { termId = teamwork, level = 5 }, Ct), HttpStatusCode.BadRequest, "skills.level");
            await ProblemAsync(await learner.PostAsJsonAsync("api/v1/me/skills", new { termId = interest, level = 2 }, Ct), HttpStatusCode.BadRequest, "skills.term");
            await JsonAsync(await learner.PostAsJsonAsync("api/v1/me/skills", new { termId = teamwork, level = 1 }, Ct));
            (await JsonAsync(await learner.PostAsJsonAsync("api/v1/me/skills", new { termId = teamwork, level = 2 }, Ct))).GetProperty("level").GetInt32().ShouldBe(2);
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{org}/skills/endorse", new { enrollmentId = enrolment, termId = fieldwork, level = 3 }, Ct),
                HttpStatusCode.Forbidden, "skills.forbidden");
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/skills/endorse", new { enrollmentId = enrolment, termId = fieldwork, level = 3 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            JsonElement[] skills = [.. (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/skills"), Ct))).EnumerateArray()];
            skills.Select(s => (s.GetProperty("name").GetString(), s.GetProperty("endorsed").GetBoolean())).ShouldBe([("Field research", true), ("Teamwork", false)]);
            skills[0].GetProperty("endorsedBy").GetString().ShouldBe(api.Data.Organisations.Single(o => o.Id == org).DisplayName);

            // A project about the activity: it must be one they took part in.
            await ProblemAsync(await learner.PostAsJsonAsync("api/v1/me/projects", new { title = "A map", listingId = Guid.NewGuid() }, Ct), HttpStatusCode.BadRequest, "projects.activity");
            JsonElement project = await JsonAsync(await learner.PostAsJsonAsync("api/v1/me/projects",
                new { title = "Mapping the mussel beds", description = "A hand-drawn map of three mussel beds.", listingId = listing }, Ct), HttpStatusCode.Created);
            Guid projectId = project.GetProperty("id").GetGuid();
            (project.GetProperty("status").GetString(), project.GetProperty("activity").GetString()).ShouldBe(("draft", activity));
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/projects/{projectId}/visibility", new { visibility = "public" }, Ct), HttpStatusCode.Conflict, "projects.not_approved");
            (await JsonAsync(await learner.PostAsync(Relative($"api/v1/me/projects/{projectId}/submit"), null, Ct))).GetProperty("status").GetString().ShouldBe("submitted");

            // Staff see it under its maker's display name only, send it back once, then approve it.
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Tobi Maps", visibility = "private" }, Ct));
            JsonElement waiting = (await JsonAsync(await admin.GetAsync(Relative($"api/v1/org/{org}/projects"), Ct))).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == projectId);
            (waiting.GetProperty("displayName").GetString(), waiting.GetRawText().Contains("Adeyemi", StringComparison.Ordinal)).ShouldBe(("Tobi Maps", false));
            (await JsonAsync(await outsider.GetAsync(Relative($"api/v1/org/{otherOrg}/projects"), Ct))).EnumerateArray().ShouldNotContain(p => p.GetProperty("id").GetGuid() == projectId);
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/projects/{projectId}/review", new { approve = false }, Ct), HttpStatusCode.BadRequest, "projects.review_note");
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/projects/{projectId}/review", new { approve = false, note = "Add a key to the map." }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            JsonElement back = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/projects"), Ct))).EnumerateArray().Single();
            (back.GetProperty("status").GetString(), back.GetProperty("reviewNote").GetString()).ShouldBe(("draft", "Add a key to the map."));
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/projects/{projectId}",
                new { title = "Mapping the mussel beds", description = "A hand-drawn map of three mussel beds, with a key.", listingId = listing }, Ct));
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/me/projects/{projectId}/submit"), null, Ct));
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{otherOrg}/projects/{projectId}/review", new { approve = true }, Ct),
                HttpStatusCode.NotFound, "projects.nothing_to_review");
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/projects/{projectId}/review", new { approve = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

            // Published, it shows on their shown portfolio with both skills; the goal never does.
            (await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/projects/{projectId}/visibility", new { visibility = "public" }, Ct)))
                .GetProperty("status").GetString().ShouldBe("published");
            string slug = "tobi-" + Guid.NewGuid().ToString("N")[..8];
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Tobi Maps", visibility = "link_only", publicSlug = slug }, Ct));
            JsonElement page = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/portfolios/{slug}"), Ct));
            page.GetProperty("projects").EnumerateArray().Single().GetProperty("title").GetString().ShouldBe("Mapping the mussel beds");
            page.GetProperty("skills").EnumerateArray().Select(s => (s.GetProperty("name").GetString(), s.GetProperty("level").GetInt32(), s.GetProperty("endorsed").GetBoolean()))
                .ShouldBe([("Field research", 3, true), ("Teamwork", 2, false)]);
            page.GetRawText().ShouldNotContain("river walk");
            page.GetRawText().ShouldNotContain(enrolment.ToString());

            // The export carries them too.
            HttpResponseMessage pdf = await learner.PostAsJsonAsync("api/v1/me/portfolio/export", new { format = "pdf" }, Ct);
            pdf.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Changed, it's a private draft again and leaves the page until staff approve it anew.
            (await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/projects/{projectId}",
                new { title = "Mapping the mussel beds again", listingId = listing }, Ct))).GetProperty("visibility").GetString().ShouldBe("private");
            (await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/portfolios/{slug}"), Ct))).GetProperty("projects").GetArrayLength().ShouldBe(0);

            // Removing a skill, even an endorsed one, is theirs to do.
            Guid endorsed = skills[0].GetProperty("id").GetGuid();
            (await learner.DeleteAsync(Relative($"api/v1/me/skills/{endorsed}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/skills"), Ct))).GetArrayLength().ShouldBe(1);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.project.reviewed' and @p = any(subject_ids)", ("p", projectId))).ShouldBe(2);
        }
    }
}
