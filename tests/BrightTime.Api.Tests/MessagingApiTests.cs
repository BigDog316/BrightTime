using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Messaging (M3b, D53): staff and families talk; a young person is never in a conversation with
/// one adult; an adult's attempt to move a young person off the platform is held; a push says only
/// that there is a message, and from which organisation.
/// </summary>
/// <remarks>
/// Uses listings no other test applies to: a builtin listing at a staff organisation that isn't
/// its organisation's first, and the first builtin listing of an organisation with no staff
/// (past the two ApplicationApiTests and WaiverApiTests take). Families are fresh accounts.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class MessagingApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private bool Approved(Guid org) => api.Data.Organisations.Single(o => o.Id == org).Status == "approved";

    private IEnumerable<(Guid Id, Guid OrgId)> Builtin() =>
        api.Data.Listings.Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && Approved(l.OrgId)).Select(l => (l.Id, l.OrgId));

    private Dictionary<Guid, string> StaffByOrg() =>
        api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).ToDictionary(u => u.Memberships.Single().OrgId, u => u.Email);

    /// <summary>A builtin listing at an organisation with staff, not the first such listing there.</summary>
    private (Guid Listing, Guid Org, string Staff) StaffedListing()
    {
        Dictionary<Guid, string> staff = StaffByOrg();
        (Guid id, Guid org) = Builtin().Where(l => staff.ContainsKey(l.OrgId)).GroupBy(l => l.OrgId).First(g => g.Count() > 1).Skip(1).First();
        return (id, org, staff[org]);
    }

    /// <summary>The first builtin listing of an organisation whose only member is its admin.</summary>
    private (Guid Listing, Guid Org) UnstaffedListing()
    {
        Dictionary<Guid, string> staff = StaffByOrg();
        List<(Guid Id, Guid OrgId)> unstaffed = [.. Builtin().Where(l => !staff.ContainsKey(l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(2).First();
        return (unstaffed.First(l => l.OrgId == org).Id, org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    /// <summary>A 15-year-old who registered from the app, with a password and a confirmed address.</summary>
    private async Task<(string Email, Guid Id)> NewTeenAsync()
    {
        string email = $"teen-{Guid.NewGuid():N}@example.org";
        using HttpClient client = api.Client();
        string birthMonth = DateTime.UtcNow.AddYears(-15).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        (await client.PostAsJsonAsync("api/v1/auth/register", new { email, birthMonth }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Guid id = Guid.Empty;
        for (int i = 0; i < 100 && id == Guid.Empty; i++)
        {
            id = await api.ScalarAsync<long>("select count(*) from identity.app_user where normalized_email = upper(@e)", ("e", email)) > 0
                ? await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", email))
                : Guid.Empty;
            if (id == Guid.Empty)
            {
                await Task.Delay(50, Ct);
            }
        }

        await api.ExecuteAsync("update identity.app_user set email_confirmed = true where id = @u", ("u", id));
        return (email, id);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    /// <summary>A submitted application for a young person, sharing their name.</summary>
    private static async Task<Guid> ApplyAsync(HttpClient family, Guid listing, string first, int age)
    {
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        string dob = DateTime.UtcNow.AddYears(-age).AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = first, lastName = "Owusu", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Guid application = (await JsonAsync(await CreateApplicationAsync(family, listing, member), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
        return application;
    }

    private static async Task<HttpResponseMessage> CreateApplicationAsync(HttpClient family, Guid listing, Guid member)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        request.Headers.Add("Idempotency-Key", "message-" + Guid.NewGuid().ToString("N"));
        return await family.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Staff_and_a_family_talk_and_the_push_says_only_who_wrote()
    {
        RequireDatabase();
        (Guid listing, Guid org, string staffEmail) = StaffedListing();
        string familyEmail = await api.NewAdultAsync();
        using HttpClient staff = await SignedInAsync(staffEmail);
        using HttpClient family = await SignedInAsync(familyEmail);
        using HttpClient stranger = await SignedInAsync(await api.NewAdultAsync());
        Guid application = await ApplyAsync(family, listing, "Esi", 10);
        Guid familyId = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", familyEmail));

        JsonElement started = await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/conversations",
            new { applicationId = application, subject = "Welcome", body = "Hello! Please bring water on day one." }, Ct), HttpStatusCode.Created);
        Guid conversation = started.GetProperty("id").GetGuid();
        started.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["guardian", "staff"]);
        started.GetProperty("readByOrganisation").GetBoolean().ShouldBeFalse();

        // The family sees it waiting, reads it, and answers.
        JsonElement mine = (await JsonAsync(await family.GetAsync(new Uri("api/v1/conversations", UriKind.Relative), Ct))).EnumerateArray().Single();
        (mine.GetProperty("id").GetGuid(), mine.GetProperty("unread").GetInt32()).ShouldBe((conversation, 1));
        JsonElement read = await JsonAsync(await family.GetAsync(new Uri($"api/v1/conversations/{conversation}", UriKind.Relative), Ct));
        read.GetProperty("messages")[0].GetProperty("body").GetString().ShouldBe("Hello! Please bring water on day one.");
        (await JsonAsync(await family.GetAsync(new Uri("api/v1/conversations", UriKind.Relative), Ct)))[0].GetProperty("unread").GetInt32().ShouldBe(0);
        (await JsonAsync(await family.PostAsJsonAsync($"api/v1/conversations/{conversation}/messages", new { body = "Thanks, we will." }, Ct), HttpStatusCode.Created))
            .GetProperty("state").GetString().ShouldBe("sent");
        JsonElement both = (await JsonAsync(await staff.GetAsync(new Uri($"api/v1/conversations/{conversation}", UriKind.Relative), Ct))).GetProperty("messages");
        (both.GetArrayLength(), both[1].GetProperty("fromMe").GetBoolean(), both[1].GetProperty("senderRole").GetString()).ShouldBe((2, false, "guardian"));

        // The family was told there is a message, and from whom; never what it says.
        string push = await api.ScalarAsync<string>(
            "select payload::text from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u limit 1",
            ("u", "%" + familyId + "%"));
        push.ShouldContain("notify.message.new");
        push.ShouldNotContain("water");

        // Nobody else reads it or opens one.
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/conversations/{conversation}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "messaging.not_found");
        await ProblemAsync(await family.PostAsJsonAsync($"api/v1/org/{org}/conversations", new { applicationId = application, body = "Hi" }, Ct), HttpStatusCode.Forbidden, "messaging.forbidden");
    }

    [Fact]
    public async Task A_young_person_is_never_alone_with_one_adult_and_an_attempt_to_move_them_off_platform_is_held()
    {
        RequireDatabase();
        (Guid listing, Guid org, string staffEmail) = StaffedListing();
        (string teenEmail, Guid teenId) = await NewTeenAsync();
        using HttpClient staff = await SignedInAsync(staffEmail);
        using HttpClient teen = await SignedInAsync(teenEmail);
        Guid application = await ApplyAsync(teen, listing, "Kofi", 15);

        // Until the young person's consent allows it, no organisation messages them.
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/conversations", new { applicationId = application, body = "Hello Kofi." }, Ct),
            HttpStatusCode.Forbidden, "messaging.not_messageable");
        await api.ExecuteAsync("insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_be_messaged_by_org) values (@u, @t, false, true, true)",
            ("u", teenId), ("t", api.Data.TenantId));

        // With it, the organisation's admin joins as an observer, and the thread says the organisation reads it.
        JsonElement started = await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/conversations",
            new { applicationId = application, body = "Hello Kofi, welcome to the club." }, Ct), HttpStatusCode.Created);
        Guid conversation = started.GetProperty("id").GetGuid();
        started.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["org_admin_observer", "staff", "youth"]);
        started.GetProperty("readByOrganisation").GetBoolean().ShouldBeTrue();
        (await api.ScalarAsync<string>("select includes_minor || '/' || adult_participant_count from community.conversation where id = @c", ("c", conversation))).ShouldBe("true/2");

        // A try at taking it elsewhere is held, and the young person never sees it.
        JsonElement held = await JsonAsync(await staff.PostAsJsonAsync($"api/v1/conversations/{conversation}/messages",
            new { body = "Text me at 914-555-0101 or add me on snapchat." }, Ct), HttpStatusCode.Created);
        held.GetProperty("state").GetString().ShouldBe("held");
        await JsonAsync(await staff.PostAsJsonAsync($"api/v1/conversations/{conversation}/messages", new { body = "See you on Tuesday at four." }, Ct), HttpStatusCode.Created);
        string[] seen = [.. (await JsonAsync(await teen.GetAsync(new Uri($"api/v1/conversations/{conversation}", UriKind.Relative), Ct))).GetProperty("messages")
            .EnumerateArray().Select(m => m.GetProperty("body").GetString()!)];
        seen.ShouldBe(["Hello Kofi, welcome to the club.", "See you on Tuesday at four."]);
        (await JsonAsync(await staff.GetAsync(new Uri($"api/v1/conversations/{conversation}", UriKind.Relative), Ct))).GetProperty("messages")
            .EnumerateArray().Count(m => m.GetProperty("state").GetString() == "held").ShouldBe(1);
        (await api.ScalarAsync<string>("select screen_result::text from community.message where conversation_id = @c and state = 'held'", ("c", conversation)))
            .ShouldContain("phone");

        // An organisation with no second adult can't open a conversation with a young person at all.
        (Guid lonely, Guid lonelyOrg) = UnstaffedListing();
        Guid second = await ApplyAsync(teen, lonely, "Kofi", 15);
        using HttpClient admin = await SignedInAsync(AdminOf(lonelyOrg));
        await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{lonelyOrg}/conversations", new { applicationId = second, body = "Hello." }, Ct),
            HttpStatusCode.Conflict, "messaging.unsafe_shape");
    }

    [Fact]
    public async Task Enrolled_families_hear_an_announcement_and_cannot_answer_it()
    {
        RequireDatabase();
        (Guid listing, Guid org, string staffEmail) = StaffedListing();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffEmail);
        using HttpClient first = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient second = await SignedInAsync(await api.NewAdultAsync());
        (string teenEmail, _) = await NewTeenAsync();
        using HttpClient teen = await SignedInAsync(teenEmail);

        // Two families and a teenager, enrolled. The teenager's consent doesn't allow messages yet.
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 50 }, Ct));
        foreach ((HttpClient family, string child, int age) in new[] { (first, "Ama", 9), (second, "Kwame", 11), (teen, "Yaw", 15) })
        {
            Guid application = await ApplyAsync(family, listing, child, age);
            await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
        }

        JsonElement sent = await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/broadcasts",
            new { listingId = listing, subject = "Rain plan", body = "We meet indoors tomorrow." }, Ct), HttpStatusCode.Created);
        (sent.GetProperty("recipients").GetInt32(), sent.GetProperty("skipped").GetInt32()).ShouldBe((2, 1));
        Guid conversation = sent.GetProperty("conversation").GetProperty("id").GetGuid();

        // A family reads it, and can't answer it; the teenager never received it.
        JsonElement mine = (await JsonAsync(await first.GetAsync(new Uri("api/v1/conversations", UriKind.Relative), Ct))).EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == conversation);
        mine.GetProperty("kind").GetString().ShouldBe("org_broadcast");
        (await JsonAsync(await second.GetAsync(new Uri($"api/v1/conversations/{conversation}", UriKind.Relative), Ct))).GetProperty("messages")[0]
            .GetProperty("body").GetString().ShouldBe("We meet indoors tomorrow.");
        await ProblemAsync(await first.PostAsJsonAsync($"api/v1/conversations/{conversation}/messages", new { body = "Thanks!" }, Ct),
            HttpStatusCode.Forbidden, "messaging.broadcast");
        (await JsonAsync(await teen.GetAsync(new Uri("api/v1/conversations", UriKind.Relative), Ct))).EnumerateArray()
            .Any(c => c.GetProperty("id").GetGuid() == conversation).ShouldBeFalse();

        // A listing with nobody enrolled has nobody to tell.
        (Guid lonely, Guid lonelyOrg) = UnstaffedListing();
        using HttpClient lonelyAdmin = await SignedInAsync(AdminOf(lonelyOrg));
        await ProblemAsync(await lonelyAdmin.PostAsJsonAsync($"api/v1/org/{lonelyOrg}/broadcasts", new { listingId = lonely, body = "Hello all." }, Ct),
            HttpStatusCode.Conflict, "messaging.no_recipients");
    }
}
