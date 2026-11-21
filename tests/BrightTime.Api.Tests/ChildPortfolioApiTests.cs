using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// A guardian's view of a child profile's portfolio (D93): nothing until their link is verified;
/// then what the child took part in, the badges they hold and the skills endorsed, read and
/// downloaded as the child's own files; never by anyone else; and gone once the link stops letting
/// them see the child's activity, or the child claims their own account.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the seventeenth organisation with no staff (QuizStepApiTests
/// takes the sixteenth); its admin accepts the child, awards the badge and endorses the skill.
/// The participation record is written as the worker would (PortfolioApiTests covers projecting it).
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class ChildPortfolioApiTests(ApiFixture api)
{
    private const string Organiser = "organiser@kettle.example.org";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org) Programme()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(16).First();
        return (unstaffed.First(l => l.OrgId == org).Id, org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<(HttpClient Client, Guid User)> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        Guid user = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", email));
        return (ApiFixture.WithBearer(api.Client(), tokens.AccessToken), user);
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    [Fact]
    public async Task A_guardian_sees_and_downloads_their_childs_portfolio_until_the_child_claims_it()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme();
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient guardian, Guid guardianId) = await SignedInAsync(await api.NewAdultAsync("guardian"));
        (HttpClient stranger, _) = await SignedInAsync(await api.NewAdultAsync());
        (HttpClient organiser, _) = await SignedInAsync(Organiser);
        using (admin)
        using (guardian)
        using (stranger)
        using (organiser)
        {
            // A child profile, under 13. Until the organiser verifies the link, there's nothing to see.
            string month = DateTime.UtcNow.AddYears(-8).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            JsonElement made = await JsonAsync(await guardian.PostAsJsonAsync("api/v1/children", new { givenName = "Nia", birthMonth = month, relationship = "parent" }, Ct), HttpStatusCode.Created);
            Guid child = made.GetProperty("id").GetGuid();
            string portfolio = $"api/v1/children/{child}/portfolio";
            await ProblemAsync(await guardian.GetAsync(Relative(portfolio), Ct), HttpStatusCode.NotFound, "portfolio.child_not_found");
            (await organiser.PostAsync(Relative($"api/v1/organiser/guardian-links/{made.GetProperty("link").GetProperty("id").GetGuid()}/attest"), null, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // The family's household, the child in it linked to the child profile, applied for and accepted.
            await JsonAsync(await guardian.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
            string dob = DateTime.UtcNow.AddYears(-8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Guid member = (await JsonAsync(await guardian.PostAsJsonAsync("api/v1/family-profile/members",
                new { role = "child", firstName = "Nia", lastName = "Boateng", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await guardian.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = child }, Ct));
            using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
            apply.Headers.Add("Idempotency-Key", "child-portfolio-" + Guid.NewGuid().ToString("N"));
            Guid application = (await JsonAsync(await guardian.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await guardian.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
            Guid enrolment = await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
            (await api.ScalarAsync<Guid>("select youth_user_id from enrollment.enrollment where id = @e", ("e", enrolment))).ShouldBe(child);

            // What the worker makes of their attendance; a badge and a skill from the organisation.
            string title = api.Data.Listings.Single(l => l.Id == listing).Title;
            await api.ExecuteAsync("""
                insert into learning.participation_record (id, tenant_id, user_id, enrollment_id, listing_id, org_id, title_snapshot, org_name_snapshot, started_on, sessions_attended, hours, source, created_at)
                values (gen_random_uuid(), @t, @u, @e, @l, @o, @title, 'The organisation', current_date - 20, 4, 6, 'attendance', now())
                """, ("t", api.Data.TenantId), ("u", child), ("e", enrolment), ("l", listing), ("o", org), ("title", title));
            Guid badge = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes",
                new { name = "Pond Dipper", description = "Found out what lives in the pond.", criteria = "Dipped the pond twice and named three creatures." }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{badge}/publish"), null, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{badge}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Created);
            Guid skill = Guid.NewGuid();
            await api.ExecuteAsync("""
                insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values (@id, @t, 'skill', @c);
                insert into platform.taxonomy_term_name (term_id, locale, name) values (@id, 'en', 'Careful looking');
                """, ("id", skill), ("t", api.Data.TenantId), ("c", "careful-looking-" + skill.ToString("N")));
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/skills/endorse", new { enrollmentId = enrolment, termId = skill, level = 2 }, Ct)).IsSuccessStatusCode.ShouldBeTrue();

            // The guardian sees all of it, and the read is recorded as theirs.
            JsonElement seen = await JsonAsync(await guardian.GetAsync(Relative(portfolio), Ct));
            seen.GetProperty("totalHours").GetDecimal().ShouldBe(6m);
            JsonElement record = seen.GetProperty("participation").EnumerateArray().Single();
            (record.GetProperty("title").GetString(), record.GetProperty("sessionsAttended").GetInt32()).ShouldBe((title, 4));
            JsonElement held = seen.GetProperty("badges").EnumerateArray().Single();
            (held.GetProperty("name").GetString(), held.GetProperty("revoked").GetBoolean()).ShouldBe(("Pond Dipper", false));
            JsonElement endorsed = seen.GetProperty("skills").EnumerateArray().Single();
            (endorsed.GetProperty("name").GetString(), endorsed.GetProperty("level").GetInt16(), endorsed.GetProperty("endorsed").GetBoolean()).ShouldBe(("Careful looking", (short)2, true));
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.portfolio.guardian_read' and actor_id = @g and subject_ids = array[@c]",
                ("g", guardianId), ("c", child))).ShouldBe(1);

            // Downloaded as the child's own files: the PDF, the badge, and a learner record.
            HttpResponseMessage pdf = await guardian.PostAsJsonAsync($"{portfolio}/export", new { format = "pdf" }, Ct);
            pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
            pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
            HttpResponseMessage badges = await guardian.PostAsJsonAsync($"{portfolio}/export", new { format = "json_ob3" }, Ct);
            (await badges.Content.ReadAsStringAsync(Ct)).ShouldContain("Pond Dipper");
            HttpResponseMessage clr = await guardian.PostAsJsonAsync($"{portfolio}/export", new { format = "clr2" }, Ct);
            clr.Content.Headers.ContentType!.MediaType.ShouldBe("application/jwt");
            await ProblemAsync(await guardian.PostAsJsonAsync($"{portfolio}/export", new { format = "zip" }, Ct), HttpStatusCode.BadRequest, "portfolio.export_format");
            (await api.ScalarAsync<long>("select count(*) from learning.portfolio_export where user_id = @c", ("c", child))).ShouldBe(3);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.portfolio.exported' and actor_id = @g and subject_ids = array[@c]",
                ("g", guardianId), ("c", child))).ShouldBe(3);

            // Nobody else: not another adult, and not through their own portfolio routes.
            await ProblemAsync(await stranger.GetAsync(Relative(portfolio), Ct), HttpStatusCode.NotFound, "portfolio.child_not_found");
            await ProblemAsync(await stranger.PostAsJsonAsync($"{portfolio}/export", new { format = "pdf" }, Ct), HttpStatusCode.NotFound, "portfolio.child_not_found");
            (await JsonAsync(await stranger.GetAsync(Relative("api/v1/me/portfolio"), Ct))).GetProperty("participation").GetArrayLength().ShouldBe(0);

            // A link that doesn't let the guardian see the child's activity shows nothing.
            await api.ExecuteAsync("update identity.guardian_link set can_view_activity = false where guardian_user_id = @g and child_user_id = @c", ("g", guardianId), ("c", child));
            await ProblemAsync(await guardian.GetAsync(Relative(portfolio), Ct), HttpStatusCode.Forbidden, "portfolio.guardian_view");
            await api.ExecuteAsync("update identity.guardian_link set can_view_activity = true where guardian_user_id = @g and child_user_id = @c", ("g", guardianId), ("c", child));

            // Claimed (D35), the portfolio is the young person's: the guardian no longer sees or downloads it.
            await api.ExecuteAsync("update identity.app_user set account_kind = 'youth' where id = @c", ("c", child));
            await ProblemAsync(await guardian.GetAsync(Relative(portfolio), Ct), HttpStatusCode.Forbidden, "portfolio.child_claimed");
            await ProblemAsync(await guardian.PostAsJsonAsync($"{portfolio}/export", new { format = "pdf" }, Ct), HttpStatusCode.Forbidden, "portfolio.child_claimed");
        }
    }
}
