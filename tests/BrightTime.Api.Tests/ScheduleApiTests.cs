using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// My schedule (D97): a guardian sees the next four weeks of meetings for everyone enrolled from
/// their household, the deadlines of what they saved or began applying for, and offers waiting;
/// a young person with their own sign-in on that household sees only their own meetings; nobody
/// else sees any. The calendar file holds the meetings once each, the deadlines, and no names.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the nineteenth organisation with no staff (RecordLogApiTests
/// takes the eighteenth). The deadlines are written to the listing for the test and removed after
/// it; the young person's account and link, and the offer, are made in the database
/// (YouthAccountTests and DecisionApiTests cover those).
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class ScheduleApiTests(ApiFixture api)
{
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
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().ElementAt(18);
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

    private static async Task<JsonElement> ScheduleAsync(HttpClient client) => await JsonAsync(await client.GetAsync(Relative("api/v1/me/schedule"), Ct));

    private static List<(string Who, DateTimeOffset StartsAt)> Meetings(JsonElement schedule, Guid listing) =>
        [.. schedule.GetProperty("meetings").EnumerateArray()
            .Where(m => m.GetProperty("listingId").GetGuid() == listing)
            .Select(m => (m.GetProperty("who").GetString()!, m.GetProperty("startsAt").GetDateTimeOffset()))];

    private static List<JsonElement> Deadlines(JsonElement schedule, Guid listing) =>
        [.. schedule.GetProperty("deadlines").EnumerateArray().Where(d => d.GetProperty("listingId").GetGuid() == listing)];

    private static async Task<Guid> MemberAsync(HttpClient guardian, string firstName, int age)
    {
        string dob = DateTime.UtcNow.AddYears(-age).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (await JsonAsync(await guardian.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "child", firstName, lastName = "Achterberg", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ApplyAsync(HttpClient guardian, Guid listing, Guid member, bool submit)
    {
        using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        apply.Headers.Add("Idempotency-Key", "schedule-" + Guid.NewGuid().ToString("N"));
        Guid application = (await JsonAsync(await guardian.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        if (submit)
        {
            await JsonAsync(await guardian.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
        }

        return application;
    }

    [Fact]
    public async Task A_household_sees_its_meetings_deadlines_and_offers_and_a_young_person_their_own()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme();
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient guardian, Guid guardianId) = await SignedInAsync(await api.NewAdultAsync("guardian"));
        string youthEmail = await api.NewAdultAsync();
        (HttpClient stranger, _) = await SignedInAsync(await api.NewAdultAsync());

        // The listing's next meeting, and deadlines in five days and in forty (past the four weeks).
        DateTimeOffset startsAt = new(DateTime.SpecifyKind(await api.ScalarAsync<DateTime>(
            "select min(starts_at) from opportunity.listing_occurrence where listing_id = @l and not is_cancelled and starts_at > now() + interval '1 hour'", ("l", listing)), DateTimeKind.Utc));
        startsAt.ShouldBeLessThan(DateTimeOffset.UtcNow + TimeSpan.FromDays(21), "the seeded listing meets every week");
        DateTimeOffset dueAt = DateTimeOffset.UtcNow.AddDays(5);
        dueAt = new DateTimeOffset(dueAt.Year, dueAt.Month, dueAt.Day, 16, 0, 0, TimeSpan.Zero);
        Guid soon = Guid.NewGuid();
        Guid late = Guid.NewGuid();
        await api.ExecuteAsync(
            "insert into opportunity.listing_deadline (id, listing_id, deadline_kind, due_at) values (@soon, @l, 'registration', @due), (@late, @l, 'scholarship', @due + interval '35 days')",
            ("l", listing), ("soon", soon), ("late", late), ("due", dueAt));
        try
        {
            using (admin)
            using (guardian)
            using (stranger)
            {
                // A household of four: two enrolled, one the young person with their own sign-in.
                await JsonAsync(await guardian.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 5 }, Ct));
                Guid ximena = await MemberAsync(guardian, "Ximena", 9);
                Guid quillon = await MemberAsync(guardian, "Quillon", 15);
                Guid zebedee = await MemberAsync(guardian, "Zebedee", 11);
                Guid odalys = await MemberAsync(guardian, "Odalys", 12);
                await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 500 }, Ct));
                foreach (Guid member in new[] { ximena, quillon })
                {
                    Guid applied = await ApplyAsync(guardian, listing, member, submit: true);
                    await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{applied}/decide", new { decision = "accept" }, Ct));
                }

                Guid youth = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", youthEmail));
                await api.ExecuteAsync("""
                    update identity.app_user set account_kind = 'youth' where id = @y;
                    delete from identity.app_user_role where user_id = @y;
                    insert into identity.app_user_role (user_id, role_id) select @y, id from identity.app_role where tenant_id = @t and normalized_name = 'YOUTH';
                    update enrollment.family_member set user_id = @y where id = @m;
                    """, ("y", youth), ("t", api.Data.TenantId), ("m", quillon));

                // Saved, the registration deadline shows on its own; the far one doesn't.
                (await guardian.PutAsJsonAsync($"api/v1/me/saved-items/{listing}", new { note = (string?)null }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
                JsonElement schedule = await ScheduleAsync(guardian);
                Meetings(schedule, listing).Where(m => m.StartsAt == startsAt).ShouldBe([("Quillon", startsAt), ("Ximena", startsAt)], ignoreOrder: true);
                List<JsonElement> deadlines = Deadlines(schedule, listing);
                deadlines.Count.ShouldBe(1);
                deadlines[0].GetProperty("kind").GetString().ShouldBe("registration");
                deadlines[0].GetProperty("dueAt").GetDateTimeOffset().ShouldBe(dueAt);
                deadlines[0].GetProperty("applicationId").ValueKind.ShouldBe(JsonValueKind.Null);
                deadlines[0].GetProperty("who").ValueKind.ShouldBe(JsonValueKind.Null);
                (await api.ScalarAsync<long>("""
                    select count(*) from platform.audit_event e
                    where e.action = 'enrollment.schedule.read' and e.actor_id = @g
                      and e.subject_ids @> (select array_agg(id) from enrollment.enrollment where family_member_id in (@x, @q) and status = 'active')
                    """, ("g", guardianId), ("x", ximena), ("q", quillon))).ShouldBe(1);

                // A draft application names the deadline by whom it's for; an offer waits for an answer.
                Guid draft = await ApplyAsync(guardian, listing, zebedee, submit: false);
                Guid offered = await ApplyAsync(guardian, listing, odalys, submit: true);
                await api.ExecuteAsync("update enrollment.application set status = 'offered' where id = @a", ("a", offered));
                schedule = await ScheduleAsync(guardian);
                deadlines = Deadlines(schedule, listing);
                deadlines.Count.ShouldBe(1);
                deadlines[0].GetProperty("applicationId").GetGuid().ShouldBe(draft);
                deadlines[0].GetProperty("who").GetString().ShouldBe("Zebedee");
                JsonElement offer = schedule.GetProperty("offers").EnumerateArray().Single();
                offer.GetProperty("applicationId").GetGuid().ShouldBe(offered);
                offer.GetProperty("who").GetString().ShouldBe("Odalys");

                // The calendar: the shared meeting once, the deadline, no one's name, not to be kept.
                HttpResponseMessage calendar = await guardian.GetAsync(Relative("api/v1/me/schedule/calendar"), Ct);
                calendar.StatusCode.ShouldBe(HttpStatusCode.OK);
                calendar.Content.Headers.ContentType!.MediaType.ShouldBe("text/calendar");
                calendar.Headers.CacheControl!.NoStore.ShouldBeTrue();
                string ics = await calendar.Content.ReadAsStringAsync(Ct);
                string uid = string.Create(CultureInfo.InvariantCulture, $"UID:{listing:N}-{startsAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}@brighttime");
                ics.Split('\n').Count(l => l.TrimEnd('\r') == uid).ShouldBe(1, "two in the household go, and the meeting is in the file once");
                ics.ShouldContain($"UID:{listing:N}-deadline-registration@brighttime");
                ics.ShouldNotContain($"deadline-scholarship");
                foreach (string name in new[] { "Ximena", "Quillon", "Zebedee", "Odalys", "Achterberg" })
                {
                    ics.ShouldNotContain(name, Case.Insensitive);
                }

                // The young person sees their own meeting, and nothing of the household's applications.
                (HttpClient own, _) = await SignedInAsync(youthEmail);
                using (own)
                {
                    JsonElement mine = await ScheduleAsync(own);
                    Meetings(mine, listing).ShouldNotBeEmpty();
                    Meetings(mine, listing).ShouldAllBe(m => m.Who == "Quillon");
                    Deadlines(mine, listing).ShouldBeEmpty();
                    mine.GetProperty("offers").GetArrayLength().ShouldBe(0);
                }

                // Nobody else sees any of it.
                JsonElement theirs = await ScheduleAsync(stranger);
                theirs.GetProperty("meetings").GetArrayLength().ShouldBe(0);
                theirs.GetProperty("deadlines").GetArrayLength().ShouldBe(0);
                theirs.GetProperty("offers").GetArrayLength().ShouldBe(0);
            }
        }
        finally
        {
            await api.ExecuteAsync("""
                delete from opportunity.listing_deadline where id in (@soon, @late);
                """, ("soon", soon), ("late", late));
        }
    }
}
