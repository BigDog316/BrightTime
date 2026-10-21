using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Api.Tests;

/// <summary>
/// Attendance (M3b, D49): a meeting's roster, records taken one by one or synced from a phone that
/// was offline, corrections that supersede, and a family's view of its own.
/// </summary>
/// <remarks>
/// The provider is a staff account (from 13 on) whose organisation has a spare builtin listing and
/// isn't DecisionApiTests' provider. Families are fresh accounts registered for the test; the
/// stranger is DecisionApiTests' provider, used only to be refused.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class AttendanceApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private List<(string Email, Guid Org)> AllStaff() =>
        [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal)
            .Select(u => (u.Email, u.Memberships.Single().OrgId))];

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private Guid? BuiltinListing(Guid org) =>
        api.Data.Organisations.Single(o => o.Id == org).Status == "approved"
            ? api.Data.Listings.FirstOrDefault(l => l.OrgId == org && l.Status == "published" && l.RegistrationMode == "builtin")?.Id
            : null;

    /// <summary>The provider (staff index), its organisation and listing, and DecisionApiTests' provider, a staff member elsewhere.</summary>
    private (int Index, Guid Org, Guid Listing, int Elsewhere) Provider()
    {
        List<(string Email, Guid Org)> staff = AllStaff();
        HashSet<Guid> taken = [.. staff.Take(13).Select(s => s.Org)];
        foreach (Guid other in api.Data.Listings.Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !taken.Contains(l.OrgId)
                     && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved").Select(l => l.OrgId).Distinct().Take(2).ToList())
        {
            taken.Add(other);
        }

        List<int> providers = [.. Enumerable.Range(13, staff.Count - 13).Where(i => !taken.Contains(staff[i].Org) && BuiltinListing(staff[i].Org) is not null)];
        int decisions = providers[0];
        int index = providers.First(i => staff[i].Org != staff[decisions].Org);
        return (index, staff[index].Org, BuiltinListing(staff[index].Org)!.Value, decisions);
    }

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

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    private sealed record Meetings(DateTimeOffset Past, DateTimeOffset Future, DateTimeOffset Old, DateTimeOffset Cancelled);

    /// <summary>
    /// Enrols each named child (applied, submitted with their name shared, accepted) and adds a
    /// session with four meetings to the listing: one two hours ago, one tomorrow, one 40 days ago,
    /// and one yesterday that was cancelled.
    /// </summary>
    private async Task<(Guid[] Enrolments, Meetings Meetings)> EnrolAsync(HttpClient admin, HttpClient staff, HttpClient family, Guid org, Guid listing, params string[] children)
    {
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 40 }, Ct));
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = children.Length + 1 }, Ct));
        List<Guid> enrolments = [];
        foreach (string child in children)
        {
            Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
                new { role = "youth", firstName = child, lastName = "Kariuki", dateOfBirth = "2014-03-03" }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            Guid application = (await JsonAsync(await CreateApplicationAsync(family, listing, member), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
            await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
            enrolments.Add(await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application)));
        }

        // Whole seconds, each test a few minutes apart, so the meetings never collide.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset at = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(-System.Security.Cryptography.RandomNumberGenerator.GetInt32(120, 600));
        Meetings meetings = new(at, at.AddDays(1).AddHours(3), at.AddDays(-40), at.AddDays(-1));
        Guid session = UuidV7.NewId();
        await api.ExecuteAsync("""
            insert into opportunity.listing_session (id, tenant_id, listing_id, timezone, starts_on, start_time, end_time)
            values (@s, @t, @l, 'America/New_York', current_date - 41, '09:00', '10:00');
            insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, starts_at, ends_at, day_of_week, is_cancelled)
            values (@t, @l, @s, @past, @past + interval '1 hour', 1, false),
                   (@t, @l, @s, @future, @future + interval '1 hour', 1, false),
                   (@t, @l, @s, @old, @old + interval '1 hour', 1, false),
                   (@t, @l, @s, @cancelled, @cancelled + interval '1 hour', 1, true);
            """,
            ("s", session), ("t", api.Data.TenantId), ("l", listing),
            ("past", meetings.Past), ("future", meetings.Future), ("old", meetings.Old), ("cancelled", meetings.Cancelled));
        return ([.. enrolments], meetings);
    }

    private static async Task<HttpResponseMessage> CreateApplicationAsync(HttpClient family, Guid listing, Guid member)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        request.Headers.Add("Idempotency-Key", "attend-" + Guid.NewGuid().ToString("N"));
        return await family.SendAsync(request, Ct);
    }

    private static async Task<Guid> RegisterAsync(HttpClient staff, Guid org, string device, HttpStatusCode expected = HttpStatusCode.Created) =>
        (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/kiosk-devices", new { deviceId = device, label = "Front desk tablet", appVersion = "1.0.0" }, Ct), expected))
            .GetProperty("id").GetGuid();

    private static string Iso(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task A_roster_takes_records_corrections_supersede_and_the_family_sees_its_own()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing, _) = Provider();
        List<(string Email, Guid Org)> staffAccounts = AllStaff();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffAccounts[index].Email);
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        (Guid[] enrolled, Meetings meetings) = await EnrolAsync(admin, staff, family, org, listing, "Zara", "Yusuf");
        Guid zara = enrolled[0];

        // The roster: everyone enrolled, by the name the family shared, nothing recorded yet.
        JsonElement roster = await JsonAsync(await staff.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/roster?occurrence={Iso(meetings.Past)}", UriKind.Relative), Ct));
        JsonElement[] lines = [.. roster.GetProperty("lines").EnumerateArray().Where(l => enrolled.Contains(l.GetProperty("enrollmentId").GetGuid()))];
        lines.Select(l => l.GetProperty("displayName").GetString()).ShouldBe(["Zara K.", "Yusuf K."], ignoreOrder: true);
        lines.ShouldAllBe(l => l.GetProperty("recordId").ValueKind == JsonValueKind.Null);

        object Record(DateTimeOffset at, string state = "present", string? note = null) => new { enrollmentId = zara, occurrenceStartsAt = at, state, note };
        JsonElement made = await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Past, note: "Arrived with a cough"), Ct), HttpStatusCode.Created);
        Guid first = made.GetProperty("id").GetGuid();
        made.GetProperty("note").GetString().ShouldBe("Arrived with a cough");
        (await api.ScalarAsync<string>("select encode(note_enc, 'hex') from attendance.attendance_record where id = @r", ("r", first)))
            .ShouldNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("cough")));

        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Past), Ct), HttpStatusCode.Conflict, "attendance.exists");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Future), Ct), HttpStatusCode.BadRequest, "attendance.too_early");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Old), Ct), HttpStatusCode.BadRequest, "attendance.too_late");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Cancelled), Ct), HttpStatusCode.BadRequest, "attendance.occurrence");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Past.AddMinutes(7)), Ct), HttpStatusCode.BadRequest, "attendance.occurrence");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance", Record(meetings.Past, "napping"), Ct), HttpStatusCode.BadRequest, "attendance.state");

        // A correction supersedes: the old record stays, pointing at the new one, and can't be corrected again.
        await ProblemAsync(await staff.PatchAsJsonAsync($"api/v1/org/{org}/attendance/{first}", new { state = "late" }, Ct), HttpStatusCode.BadRequest, "attendance.reason");
        JsonElement corrected = await JsonAsync(await staff.PatchAsJsonAsync($"api/v1/org/{org}/attendance/{first}",
            new { state = "late", minutesAttended = 40, reason = "Arrived 20 minutes late" }, Ct));
        Guid second = corrected.GetProperty("id").GetGuid();
        corrected.GetProperty("state").GetString().ShouldBe("late");
        (await api.ScalarAsync<Guid>("select superseded_by from attendance.attendance_record where id = @r", ("r", first))).ShouldBe(second);
        await ProblemAsync(await staff.PatchAsJsonAsync($"api/v1/org/{org}/attendance/{first}", new { state = "present", reason = "Again" }, Ct), HttpStatusCode.Conflict, "attendance.superseded");

        string window = $"from={Iso(meetings.Past.AddDays(-1))}&to={Iso(meetings.Past.AddDays(1))}&listingId={listing}";
        JsonElement current = await JsonAsync(await staff.GetAsync(new Uri($"api/v1/org/{org}/attendance?{window}", UriKind.Relative), Ct));
        current.EnumerateArray().Where(r => r.GetProperty("enrollmentId").GetGuid() == zara).Select(r => r.GetProperty("id").GetGuid()).ShouldBe([second]);
        JsonElement history = await JsonAsync(await staff.GetAsync(new Uri($"api/v1/org/{org}/attendance?{window}&history=true", UriKind.Relative), Ct));
        history.EnumerateArray().Count(r => r.GetProperty("enrollmentId").GetGuid() == zara).ShouldBe(2);
        await ProblemAsync(await staff.GetAsync(new Uri($"api/v1/org/{org}/attendance?from={Iso(meetings.Past.AddDays(-40))}&to={Iso(meetings.Past)}", UriKind.Relative), Ct),
            HttpStatusCode.BadRequest, "attendance.range");

        // The family sees Zara's current record, without the staff's note.
        JsonElement mine = await JsonAsync(await family.GetAsync(new Uri("api/v1/me/attendance", UriKind.Relative), Ct));
        JsonElement seen = mine.EnumerateArray().Single(r => r.GetProperty("enrollmentId").GetGuid() == zara);
        seen.GetProperty("memberFirstName").GetString().ShouldBe("Zara");
        seen.GetProperty("state").GetString().ShouldBe("late");
        seen.TryGetProperty("note", out _).ShouldBeFalse();

        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'attendance.corrected' and actor_org_id = @o and detail->>'reason' = 'Arrived 20 minutes late'",
            ("o", org))).ShouldBe(1);
    }

    [Fact]
    public async Task An_offline_batch_reports_each_item_and_a_replay_changes_nothing()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing, int elsewhere) = Provider();
        List<(string Email, Guid Org)> staffAccounts = AllStaff();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffAccounts[index].Email);
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        (string strangerEmail, _) = staffAccounts[elsewhere];
        using HttpClient stranger = await SignedInAsync(strangerEmail);
        (Guid[] enrolled, Meetings meetings) = await EnrolAsync(admin, staff, family, org, listing, "Wanjiru", "Otieno");
        (Guid wanjiru, Guid otieno) = (enrolled[0], enrolled[1]);

        string device = "phone-" + Guid.NewGuid().ToString("N")[..8];
        await RegisterAsync(staff, org, device);
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid(), c4 = Guid.NewGuid();
        object Batch(string onDevice, params object[] items) => new { deviceId = onDevice, appVersion = "1.0.0", items };
        object Item(Guid client, Guid enrolment, string state) =>
            new { clientRecordId = client, enrollmentId = enrolment, occurrenceStartsAt = meetings.Past, state, capturedAt = meetings.Past.AddMinutes(5) };
        object batch = Batch(device, Item(c1, wanjiru, "present"), Item(c2, otieno, "absent"), Item(c3, wanjiru, "late"), Item(c4, otieno, "asleep"));

        JsonElement[] first = [.. (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch", batch, Ct))).EnumerateArray()];
        first.Select(o => o.GetProperty("status").GetString()).ShouldBe(["applied", "applied", "conflict", "rejected"]);
        first[3].GetProperty("reason").GetString().ShouldBe("attendance.state");
        first[2].GetProperty("serverId").GetGuid().ShouldBe(first[0].GetProperty("serverId").GetGuid());

        // The phone replays its queue: nothing new is written, and each item says why.
        JsonElement[] replay = [.. (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch", batch, Ct))).EnumerateArray()];
        replay.Select(o => o.GetProperty("status").GetString()).ShouldBe(["duplicate", "duplicate", "conflict", "rejected"]);
        replay[0].GetProperty("serverId").GetGuid().ShouldBe(first[0].GetProperty("serverId").GetGuid());
        (await api.ScalarAsync<long>("select count(*) from attendance.attendance_record where enrollment_id in (@w, @o)", ("w", wanjiru), ("o", otieno))).ShouldBe(2);
        (await api.ScalarAsync<long>("select count(*) from attendance.attendance_sync_batch where device_id = @d", ("d", device))).ShouldBe(2);
        (await api.ScalarAsync<string>("select method from attendance.attendance_record where client_record_id = @c", ("c", c1))).ShouldBe("offline_sync");

        // Another phone that marked Otieno too finds the first record stands.
        string otherPhone = "phone-other-" + Guid.NewGuid().ToString("N")[..6];
        await RegisterAsync(staff, org, otherPhone);
        JsonElement[] other = [.. (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch",
            Batch(otherPhone, Item(Guid.NewGuid(), otieno, "present")), Ct))).EnumerateArray()];
        other.Single().GetProperty("status").GetString().ShouldBe("conflict");

        // Nobody outside the organisation's staff takes or sees its attendance.
        await ProblemAsync(await stranger.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch", batch, Ct), HttpStatusCode.Forbidden, "attendance.forbidden");
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/roster?occurrence={Iso(meetings.Past)}", UriKind.Relative), Ct),
            HttpStatusCode.Forbidden, "attendance.forbidden");
        await ProblemAsync(await family.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/roster?occurrence={Iso(meetings.Past)}", UriKind.Relative), Ct),
            HttpStatusCode.Forbidden, "attendance.forbidden");
    }

    /// <summary>A meeting under way now: started ten minutes ago, ending in fifty.</summary>
    private async Task<DateTimeOffset> MeetingNowAsync(Guid listing)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset at = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(-10);
        Guid session = UuidV7.NewId();
        await api.ExecuteAsync("""
            insert into opportunity.listing_session (id, tenant_id, listing_id, timezone, starts_on, start_time, end_time)
            values (@s, @t, @l, 'America/New_York', current_date - 1, '09:00', '10:00');
            insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, starts_at, ends_at, day_of_week, is_cancelled)
            values (@t, @l, @s, @at, @at + interval '1 hour', 1, false);
            """, ("s", session), ("t", api.Data.TenantId), ("l", listing), ("at", at));
        return at;
    }

    [Fact]
    public async Task Staff_scan_a_familys_badge_once_and_the_badge_names_nobody()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing, int elsewhere) = Provider();
        List<(string Email, Guid Org)> staffAccounts = AllStaff();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffAccounts[index].Email);
        using HttpClient stranger = await SignedInAsync(staffAccounts[elsewhere].Email);
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient otherFamily = await SignedInAsync(await api.NewAdultAsync());
        (Guid[] enrolled, Meetings meetings) = await EnrolAsync(admin, staff, family, org, listing, "Amani");
        Guid amani = enrolled[0];

        JsonElement badge = await JsonAsync(await family.PostAsJsonAsync("api/v1/me/attendance/badges", new { enrollmentId = amani }, Ct), HttpStatusCode.Created);
        string token = badge.GetProperty("token").GetString()!;
        token.Length.ShouldBe(43);
        token.ShouldNotContain(amani.ToString("N")[..8], Case.Insensitive);
        (badge.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
        await ProblemAsync(await otherFamily.PostAsJsonAsync("api/v1/me/attendance/badges", new { enrollmentId = amani }, Ct), HttpStatusCode.NotFound, "checkin.enrolment");

        // Another organisation's staff can't use it; this organisation's staff spend it once.
        await ProblemAsync(await stranger.PostAsJsonAsync($"api/v1/org/{org}/attendance/scan", new { token, occurrenceStartsAt = meetings.Past }, Ct), HttpStatusCode.Forbidden, "attendance.forbidden");
        JsonElement scanned = await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/scan", new { token, occurrenceStartsAt = meetings.Past }, Ct), HttpStatusCode.Created);
        scanned.GetProperty("enrollmentId").GetGuid().ShouldBe(amani);
        scanned.GetProperty("method").GetString().ShouldBe("qr");
        scanned.GetProperty("state").GetString().ShouldBe("present");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/scan", new { token, occurrenceStartsAt = meetings.Past }, Ct), HttpStatusCode.Conflict, "checkin.used");

        // A stale badge, and something that isn't a badge at all.
        string stale = (await JsonAsync(await family.PostAsJsonAsync("api/v1/me/attendance/badges", new { enrollmentId = amani }, Ct), HttpStatusCode.Created)).GetProperty("token").GetString()!;
        await api.ExecuteAsync("update attendance.checkin_token set issued_at = now() - interval '1 hour', expires_at = now() - interval '58 minutes' where token_hash = sha256(convert_to(@t, 'UTF8'))", ("t", stale));
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/scan", new { token = stale, occurrenceStartsAt = meetings.Past }, Ct), HttpStatusCode.Conflict, "checkin.expired");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/scan", new { token = new string('x', 43), occurrenceStartsAt = meetings.Past }, Ct), HttpStatusCode.NotFound, "checkin.invalid");
        (await api.ScalarAsync<long>("select count(*) from attendance.checkin_token where length(token_hash) = 32 and enrollment_id = @e", ("e", amani))).ShouldBe(2);
    }

    [Fact]
    public async Task A_family_checks_in_at_a_kiosk_for_the_meeting_on_now()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing, _) = Provider();
        List<(string Email, Guid Org)> staffAccounts = AllStaff();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffAccounts[index].Email);
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient otherFamily = await SignedInAsync(await api.NewAdultAsync());
        (Guid[] enrolled, _) = await EnrolAsync(admin, staff, family, org, listing, "Baraka", "Neema");
        DateTimeOffset now = await MeetingNowAsync(listing);

        string kiosk = (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/checkin-tokens", new { listingId = listing }, Ct), HttpStatusCode.Created)).GetProperty("token").GetString()!;

        // One code on the screen serves every family that scans it while it lasts.
        JsonElement baraka = await JsonAsync(await family.PostAsJsonAsync("api/v1/attendance/checkin", new { token = kiosk, enrollmentId = enrolled[0] }, Ct), HttpStatusCode.Created);
        baraka.GetProperty("method").GetString().ShouldBe("kiosk");
        baraka.GetProperty("occurrenceStartsAt").GetDateTimeOffset().ShouldBe(now);
        await JsonAsync(await family.PostAsJsonAsync("api/v1/attendance/checkin", new { token = kiosk, enrollmentId = enrolled[1] }, Ct), HttpStatusCode.Created);
        await ProblemAsync(await family.PostAsJsonAsync("api/v1/attendance/checkin", new { token = kiosk, enrollmentId = enrolled[0] }, Ct), HttpStatusCode.Conflict, "attendance.exists");

        // Only for one's own young people, and only with a kiosk's code while it lasts.
        await ProblemAsync(await otherFamily.PostAsJsonAsync("api/v1/attendance/checkin", new { token = kiosk, enrollmentId = enrolled[0] }, Ct), HttpStatusCode.NotFound, "checkin.enrolment");
        string badge = (await JsonAsync(await family.PostAsJsonAsync("api/v1/me/attendance/badges", new { enrollmentId = enrolled[0] }, Ct), HttpStatusCode.Created)).GetProperty("token").GetString()!;
        await ProblemAsync(await family.PostAsJsonAsync("api/v1/attendance/checkin", new { token = badge, enrollmentId = enrolled[0] }, Ct), HttpStatusCode.NotFound, "checkin.invalid");
        await api.ExecuteAsync("update attendance.checkin_token set issued_at = now() - interval '5 minutes', expires_at = now() - interval '4 minutes' where token_hash = sha256(convert_to(@t, 'UTF8'))", ("t", kiosk));
        await ProblemAsync(await family.PostAsJsonAsync("api/v1/attendance/checkin", new { token = kiosk, enrollmentId = enrolled[1] }, Ct), HttpStatusCode.Conflict, "checkin.expired");
    }

    [Fact]
    public async Task A_revoked_device_is_told_to_wipe_and_syncs_nothing()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing, int elsewhere) = Provider();
        List<(string Email, Guid Org)> staffAccounts = AllStaff();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient staff = await SignedInAsync(staffAccounts[index].Email);
        using HttpClient stranger = await SignedInAsync(staffAccounts[elsewhere].Email);
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anyone = api.Client();
        (Guid[] enrolled, Meetings meetings) = await EnrolAsync(admin, staff, family, org, listing, "Imani");
        object Batch(string device) => new
        {
            deviceId = device,
            items = new[] { new { clientRecordId = Guid.NewGuid(), enrollmentId = enrolled[0], occurrenceStartsAt = meetings.Past, state = "present" } },
        };

        // An unregistered phone can't sync; registered, it can, and registering again changes nothing.
        string device = "tablet-" + Guid.NewGuid().ToString("N");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch", Batch(device), Ct), HttpStatusCode.Forbidden, "device.unknown");
        Guid id = await RegisterAsync(staff, org, device);
        (await RegisterAsync(staff, org, device, HttpStatusCode.OK)).ShouldBe(id);
        JsonElement contact = await JsonAsync(await anyone.PostAsJsonAsync("api/v1/attendance/devices/contact", new { deviceId = device }, Ct));
        (contact.GetProperty("registered").GetBoolean(), contact.GetProperty("wipe").GetBoolean(), contact.GetProperty("maxOfflineHours").GetInt32()).ShouldBe((true, false, 72));
        (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch", Batch(device), Ct))).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("applied");

        // Admins see the organisation's devices; staff don't.
        (await JsonAsync(await admin.GetAsync(new Uri($"api/v1/org/{org}/kiosk-devices", UriKind.Relative), Ct))).EnumerateArray()
            .Any(d => d.GetProperty("id").GetGuid() == id).ShouldBeTrue();
        await ProblemAsync(await staff.GetAsync(new Uri($"api/v1/org/{org}/kiosk-devices", UriKind.Relative), Ct), HttpStatusCode.Forbidden, "form.forbidden");

        // Lost: revoked with a reason, it is told to wipe, even without a sign-in, and syncs nothing.
        await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/kiosk-devices/{id}/revoke", new { reason = "" }, Ct), HttpStatusCode.BadRequest, "device.reason");
        JsonElement revoked = await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/kiosk-devices/{id}/revoke", new { reason = "Left on the bus" }, Ct));
        revoked.GetProperty("wipeRequested").GetBoolean().ShouldBeTrue();
        (await JsonAsync(await anyone.PostAsJsonAsync("api/v1/attendance/devices/contact", new { deviceId = device }, Ct))).GetProperty("wipe").GetBoolean().ShouldBeTrue();
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/attendance/batch", Batch(device), Ct), HttpStatusCode.Forbidden, "device.revoked");
        await ProblemAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/kiosk-devices", new { deviceId = device, label = "Again" }, Ct), HttpStatusCode.Conflict, "device.revoked");

        // A device belongs to one organisation; staff revoke only what they registered.
        string second = "tablet-" + Guid.NewGuid().ToString("N");
        Guid secondId = await RegisterAsync(staff, org, second);
        await ProblemAsync(await stranger.PostAsJsonAsync($"api/v1/org/{staffAccounts[elsewhere].Org}/kiosk-devices", new { deviceId = second, label = "Mine now" }, Ct),
            HttpStatusCode.Conflict, "device.taken");
        await ProblemAsync(await stranger.PostAsJsonAsync($"api/v1/org/{staffAccounts[elsewhere].Org}/kiosk-devices/{secondId}/revoke", new { reason = "Not mine" }, Ct),
            HttpStatusCode.NotFound, "device.not_found");
        (await JsonAsync(await staff.PostAsJsonAsync($"api/v1/org/{org}/kiosk-devices/{secondId}/revoke", new { reason = "Replaced" }, Ct)))
            .GetProperty("revokedReason").GetString().ShouldBe("Replaced");
        (await JsonAsync(await anyone.PostAsJsonAsync("api/v1/attendance/devices/contact", new { deviceId = "never-seen-" + Guid.NewGuid().ToString("N") }, Ct)))
            .GetProperty("registered").GetBoolean().ShouldBeFalse();
    }
}
