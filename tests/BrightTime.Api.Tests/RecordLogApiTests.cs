using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// "Who has seen my record" (D96): a guardian sees, for their child profile, every time someone
/// else read or changed the child's records (the organisation deciding the application, awarding a
/// badge and endorsing a skill, named by organisation and role; the organiser verifying the link;
/// another organisation turned away) but not their own. Nobody else sees it, and each look is
/// audited. Once the child has their own account the log is theirs, and their guardian's reads
/// are in it.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the eighteenth organisation with no staff
/// (ChildPortfolioApiTests takes the seventeenth). The refused read is written as
/// YouthRecordHandler writes one; the claim is done in the database (YouthAccountTests covers it).
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class RecordLogApiTests(ApiFixture api)
{
    private const string Organiser = "organiser@kettle.example.org";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org, Guid Other) Programme()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid[] orgs = [.. unstaffed.Select(l => l.OrgId).Distinct()];
        Guid org = orgs[17];
        return (unstaffed.First(l => l.OrgId == org).Id, org, orgs[0]);
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

    private sealed record Entry(string What, bool Refused, string Who, string? Org, string? Role, string? Title, string? Basis);

    private static List<Entry> Entries(JsonElement page) =>
        [.. page.GetProperty("entries").EnumerateArray().Select(e => new Entry(
            e.GetProperty("what").GetString()!,
            e.GetProperty("refused").GetBoolean(),
            e.GetProperty("who").GetString()!,
            e.GetProperty("organisation").GetString(),
            e.GetProperty("role").GetString(),
            e.GetProperty("title").GetString(),
            e.GetProperty("basis").GetString()))];

    [Fact]
    public async Task A_guardian_sees_who_read_or_changed_their_childs_record_and_then_the_young_person_does()
    {
        RequireDatabase();
        (Guid listing, Guid org, Guid other) = Programme();
        string orgName = api.Data.Organisations.Single(o => o.Id == org).DisplayName;
        string otherName = api.Data.Organisations.Single(o => o.Id == other).DisplayName;
        (HttpClient admin, Guid adminId) = await SignedInAsync(AdminOf(org));
        (HttpClient guardian, Guid guardianId) = await SignedInAsync(await api.NewAdultAsync("guardian"));
        (HttpClient stranger, Guid strangerId) = await SignedInAsync(await api.NewAdultAsync());
        (HttpClient organiser, _) = await SignedInAsync(Organiser);
        Guid otherAdmin = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", AdminOf(other)));
        using (admin)
        using (guardian)
        using (stranger)
        using (organiser)
        {
            // A child profile, its link verified by the organiser, applied for and accepted.
            string month = DateTime.UtcNow.AddYears(-8).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            JsonElement made = await JsonAsync(await guardian.PostAsJsonAsync("api/v1/children", new { givenName = "Ada", birthMonth = month, relationship = "parent" }, Ct), HttpStatusCode.Created);
            Guid child = made.GetProperty("id").GetGuid();
            string log = $"api/v1/children/{child}/record-log";
            await ProblemAsync(await guardian.GetAsync(Relative(log), Ct), HttpStatusCode.NotFound, "record_log.child_not_found");
            (await organiser.PostAsync(Relative($"api/v1/organiser/guardian-links/{made.GetProperty("link").GetProperty("id").GetGuid()}/attest"), null, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            await JsonAsync(await guardian.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
            string dob = DateTime.UtcNow.AddYears(-8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Guid member = (await JsonAsync(await guardian.PostAsJsonAsync("api/v1/family-profile/members",
                new { role = "child", firstName = "Ada", lastName = "Okafor", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await guardian.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = child }, Ct));
            using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
            apply.Headers.Add("Idempotency-Key", "record-log-" + Guid.NewGuid().ToString("N"));
            Guid application = (await JsonAsync(await guardian.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await guardian.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
            Guid enrolment = await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));

            // A badge and a skill from the organisation.
            Guid badge = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes",
                new { name = "Log Reader", description = "Read the log.", criteria = "Read the whole log twice and said what it showed." }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{badge}/publish"), null, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{badge}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Created);
            Guid skill = Guid.NewGuid();
            await api.ExecuteAsync("""
                insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values (@id, @t, 'skill', @c);
                insert into platform.taxonomy_term_name (term_id, locale, name) values (@id, 'en', 'Keeping records');
                """, ("id", skill), ("t", api.Data.TenantId), ("c", "keeping-records-" + skill.ToString("N")));
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/skills/endorse", new { enrollmentId = enrolment, termId = skill, level = 1 }, Ct)).IsSuccessStatusCode.ShouldBeTrue();

            await api.ExecuteAsync("update directory.org_membership set title = 'Program Director' where org_id = @o and user_id = @u", ("o", org), ("u", adminId));

            // Another organisation's admin, turned away, as YouthRecordHandler records it.
            await api.ExecuteAsync("""
                insert into platform.audit_event (id, occurred_at, tenant_id, actor_id, actor_kind, actor_org_id, action, outcome, subject_kind, subject_ids, subject_count, reason)
                values (gen_random_uuid(), now(), @t, @a, 'user', @o, 'identity.youth_record.read', 'deny', 'youth', array[@c], 1, 'youth_record.no_grant')
                """, ("t", api.Data.TenantId), ("a", otherAdmin), ("o", other), ("c", child));

            // The guardian sees each of them, newest first, and none of their own. The admin is named
            // by the job title they were given when invited, with their role.
            JsonElement page = await JsonAsync(await guardian.GetAsync(Relative(log), Ct));
            List<Entry> entries = Entries(page);
            entries[0].ShouldBe(new Entry("record_read", true, "staff", otherName, "owner", null, null), "the refusal is the newest");
            entries.ShouldContain(e => e.What == "application_decided" && !e.Refused && e.Who == "staff" && e.Org == orgName && e.Role == "owner" && e.Title == "Program Director");
            entries.ShouldContain(e => e.What == "badge_awarded" && e.Who == "staff" && e.Org == orgName);
            entries.ShouldContain(e => e.What == "skill_endorsed" && e.Who == "staff" && e.Org == orgName);
            entries.ShouldContain(e => e.What == "link_verified" && e.Who == "organiser" && e.Org == null);
            entries.ShouldNotContain(e => e.Who == "guardian", "the viewer's own actions are left out");
            entries.ShouldNotContain(e => e.What == "other", "every action that reached this log is worded");
            page.GetProperty("older").ValueKind.ShouldBe(JsonValueKind.Null);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'identity.record_log.read' and actor_id = @g and subject_ids = array[@c] and outcome = 'allow'",
                ("g", guardianId), ("c", child))).ShouldBe(1);

            // Nobody else, and that's recorded too; the admin's own log has none of the child's.
            await ProblemAsync(await stranger.GetAsync(Relative(log), Ct), HttpStatusCode.NotFound, "record_log.child_not_found");
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'identity.record_log.read' and actor_id = @s and subject_ids = array[@c] and outcome = 'deny'",
                ("s", strangerId), ("c", child))).ShouldBe(1);
            await ProblemAsync(await admin.GetAsync(Relative(log), Ct), HttpStatusCode.NotFound, "record_log.child_not_found");

            // A link that doesn't let the guardian see the child's activity shows nothing.
            await api.ExecuteAsync("update identity.guardian_link set can_view_activity = false where guardian_user_id = @g and child_user_id = @c", ("g", guardianId), ("c", child));
            await ProblemAsync(await guardian.GetAsync(Relative(log), Ct), HttpStatusCode.Forbidden, "record_log.guardian_view");
            await api.ExecuteAsync("update identity.guardian_link set can_view_activity = true where guardian_user_id = @g and child_user_id = @c", ("g", guardianId), ("c", child));

            // Claimed, the log is the young person's: the guardian is turned away, and the young
            // person sees what the organisation did and what their guardian did, but not their own look.
            string own = $"ada-{Guid.NewGuid():N}@example.org";
            await api.ExecuteAsync("""
                update identity.app_user set account_kind = 'youth', email = @e, normalized_email = upper(@e), user_name = @e, normalized_user_name = upper(@e), email_confirmed = true where id = @c;
                delete from identity.app_user_role where user_id = @c;
                insert into identity.app_user_role (user_id, role_id) select @c, id from identity.app_role where tenant_id = @t and normalized_name = 'YOUTH';
                """, ("e", own), ("c", child), ("t", api.Data.TenantId));
            await ProblemAsync(await guardian.GetAsync(Relative(log), Ct), HttpStatusCode.Forbidden, "record_log.child_claimed");
            (HttpClient youth, _) = await SignedInAsync(own);
            using (youth)
            {
                await JsonAsync(await youth.GetAsync(Relative("api/v1/me/portfolio"), Ct));
                List<Entry> mine = Entries(await JsonAsync(await youth.GetAsync(Relative("api/v1/me/record-log"), Ct)));
                mine.ShouldContain(e => e.What == "application_decided" && e.Who == "staff" && e.Org == orgName);
                mine.ShouldContain(e => e.What == "record_log_read" && e.Who == "guardian", "the guardian's look at the log is a read of the record");
                mine.ShouldContain(e => e.What == "family_changed" && e.Who == "guardian");
                mine.ShouldNotContain(e => e.What == "portfolio_read", "their own look at their portfolio isn't listed");
            }

            // An adult's own log holds only what others did to their records.
            (await JsonAsync(await admin.GetAsync(Relative("api/v1/me/record-log"), Ct))).GetProperty("entries").EnumerateArray()
                .ShouldNotContain(e => e.GetProperty("who").GetString() == "staff" && e.GetProperty("organisation").GetString() == orgName);
        }
    }
}
