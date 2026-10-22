using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrightTime.SharedKernel.Ids;
using ClosedXML.Excel;

namespace BrightTime.Api.Tests;

/// <summary>
/// Provider reports (M3b, D57): a listing's roster and attendance name young people as the
/// family's share allows; participation only counts them, and hides small counts; each run is
/// recorded and audited, and nothing rendered is kept.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the sixth organisation with no staff (PaymentApiTests takes
/// the fourth and fifth); its admin decides and runs the reports. Families are fresh accounts.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class ReportApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org, Guid OtherListing) Listing()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(5).First();
        return (unstaffed.First(l => l.OrgId == org).Id, org, unstaffed.First(l => l.OrgId != org).Id);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

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

    private static async Task<byte[]> FileAsync(HttpResponseMessage response, string contentType)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe(contentType);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue("a report can name young people");
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    /// <summary>The file's rows, quotes undone (no field in these reports spans lines).</summary>
    private static string[][] Csv(byte[] bytes) =>
        [.. Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split("\r\n").Select(Fields)];

    private static string[] Fields(string line)
    {
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                field.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }

    private async Task<Guid> EnrolAsync(HttpClient admin, HttpClient family, Guid org, Guid listing, string child, string[] shareFields)
    {
        Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = child, lastName = "Owusu", dateOfBirth = "2015-05-05" }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        request.Headers.Add("Idempotency-Key", "report-" + Guid.NewGuid().ToString("N"));
        Guid application = (await JsonAsync(await family.SendAsync(request, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields }, Ct));
        await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
        return await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
    }

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, Guid org, object body) =>
        client.PostAsJsonAsync($"api/v1/org/{org}/reports/runs", body, Ct);

    [Fact]
    public async Task Staff_get_named_rosters_and_attendance_and_participation_with_small_counts_hidden()
    {
        RequireDatabase();
        (Guid listing, Guid org, Guid otherListing) = Listing();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient stranger = await SignedInAsync(await api.NewAdultAsync());

        // Three children, two still named to the organisation; two meetings, yesterday and two hours ago.
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 30 }, Ct));
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 4 }, Ct));
        Guid ama = await EnrolAsync(admin, family, org, listing, "Ama", ["member_names"]);
        Guid kofi = await EnrolAsync(admin, family, org, listing, "Kofi", ["member_names"]);
        using HttpClient otherFamily = await SignedInAsync(await api.NewAdultAsync());
        await JsonAsync(await otherFamily.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        Guid unnamed = await EnrolAsync(admin, otherFamily, org, listing, "Esi", ["member_names"]);

        // The second family withdraws its share: the organisation keeps the enrolment, not the name.
        foreach (JsonElement share in (await JsonAsync(await otherFamily.GetAsync(new Uri("api/v1/family-profile/shares", UriKind.Relative), Ct))).EnumerateArray())
        {
            (await otherFamily.DeleteAsync(new Uri($"api/v1/family-profile/shares/{share.GetProperty("id").GetGuid()}", UriKind.Relative), Ct)).IsSuccessStatusCode.ShouldBeTrue();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset recent = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-2).AddMinutes(-System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 59));
        DateTimeOffset earlier = recent.AddDays(-1);
        Guid session = UuidV7.NewId();
        await api.ExecuteAsync("""
            insert into opportunity.listing_session (id, tenant_id, listing_id, timezone, starts_on, start_time, end_time)
            values (@s, @t, @l, 'America/New_York', current_date - 3, '09:00', '10:00');
            insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, starts_at, ends_at, day_of_week, is_cancelled)
            values (@t, @l, @s, @recent, @recent + interval '1 hour', 1, false),
                   (@t, @l, @s, @earlier, @earlier + interval '1 hour', 1, false);
            """, ("s", session), ("t", api.Data.TenantId), ("l", listing), ("recent", recent), ("earlier", earlier));
        foreach ((Guid enrolment, DateTimeOffset at, string state) in new[]
                 { (ama, earlier, "present"), (ama, recent, "present"), (kofi, earlier, "absent"), (kofi, recent, "late"), (unnamed, recent, "present") })
        {
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/attendance", new { enrollmentId = enrolment, occurrenceStartsAt = at, state }, Ct), HttpStatusCode.Created);
        }

        // The catalogue, and who may not run reports.
        JsonElement catalogue = await JsonAsync(await admin.GetAsync(new Uri($"api/v1/org/{org}/reports/catalogue", UriKind.Relative), Ct));
        catalogue.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ShouldBe(["provider.roster", "provider.attendance", "provider.participation"]);
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/org/{org}/reports/catalogue", UriKind.Relative), Ct), HttpStatusCode.Forbidden, "reports.forbidden");
        await ProblemAsync(await RunAsync(stranger, org, new { reportKey = "provider.roster", listingId = listing, format = "csv" }), HttpStatusCode.Forbidden, "reports.forbidden");
        await ProblemAsync(await RunAsync(admin, org, new { reportKey = "provider.everything", format = "csv" }), HttpStatusCode.BadRequest, "reports.unknown");
        await ProblemAsync(await RunAsync(admin, org, new { reportKey = "provider.roster", listingId = listing, format = "docx" }), HttpStatusCode.BadRequest, "reports.format");
        await ProblemAsync(await RunAsync(admin, org, new { reportKey = "provider.roster", format = "csv" }), HttpStatusCode.BadRequest, "reports.listing");
        await ProblemAsync(await RunAsync(admin, org, new { reportKey = "provider.roster", listingId = otherListing, format = "csv" }), HttpStatusCode.NotFound, "form.listing_not_found");
        await ProblemAsync(await RunAsync(admin, org, new { reportKey = "provider.attendance", listingId = listing, from = now.AddDays(-40), to = now, format = "csv" }),
            HttpStatusCode.BadRequest, "reports.range");

        // The roster: names where shared, attendance since enrolling.
        string[][] roster = Csv(await FileAsync(await RunAsync(admin, org, new { reportKey = "provider.roster", listingId = listing, format = "csv" }), "text/csv"));
        roster[0][0].ShouldBe("Roster");
        string[][] people = [.. roster.SkipWhile(r => r[0] != "Name").Skip(1).TakeWhile(r => r.Length == 4)];
        people.Select(r => (r[0], r[2], r[3])).ShouldBe([("Ama O.", "2", "2"), ("Kofi O.", "1", "2"), ("Name not shared", "1", "1")], ignoreOrder: true);
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'reports.provider.roster' and subject_ids @> array[@a, @k, @u]",
            ("a", ama), ("k", kofi), ("u", unnamed))).ShouldBe(1);

        // Attendance, as a spreadsheet: each young person against each meeting.
        byte[] xlsx = await FileAsync(await RunAsync(admin, org, new { reportKey = "provider.attendance", listingId = listing, from = earlier.AddHours(-1), to = now, format = "xlsx" }),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using (XLWorkbook book = new(new MemoryStream(xlsx)))
        {
            IXLWorksheet sheet = book.Worksheets.Single();
            IXLRow header = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Name");
            Dictionary<string, string[]> grid = sheet.RowsUsed().Where(r => r.RowNumber() > header.RowNumber() && r.Cell(1).GetString().Length > 0 && r.Cell(4).GetString().Length > 0)
                .ToDictionary(r => r.Cell(1).GetString(), r => new[] { r.Cell(2).GetString(), r.Cell(3).GetString(), r.Cell(4).GetString() });
            grid["Ama O."].ShouldBe(["P", "P", "2"]);
            grid["Kofi O."].ShouldBe(["A", "L", "1"]);
            grid["Name not shared"].ShouldBe(["", "P", "1"]);
            sheet.Cell(header.RowNumber() + 1, 4).DataType.ShouldBe(XLDataType.Number);
        }

        // Participation counts only, and three young people is too few to show.
        string[][] participation = Csv(await FileAsync(await RunAsync(admin, org, new { reportKey = "provider.participation", from = now.AddDays(-7), to = now, format = "csv" }), "text/csv"));
        string title = api.Data.Listings.Single(l => l.Id == listing).Title;
        string[] activity = participation.Single(r => r[0] == title);
        activity[1..].ShouldBe(["2", "<10", "<10", "<10", "<10"]);
        participation.First(r => r[0] == "Total")[2].ShouldBe("<10");
        participation.Any(r => r[0].StartsWith("To protect privacy, a count under 10", StringComparison.Ordinal) && r[0].Contains("figures are hidden here", StringComparison.Ordinal)).ShouldBeTrue();
        participation.SelectMany(r => r).ShouldNotContain("Ama O.");

        // A PDF too; and every run is on record, none with a stored file.
        byte[] pdf = await FileAsync(await RunAsync(admin, org, new { reportKey = "provider.participation", format = "pdf" }), "application/pdf");
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        (await api.ScalarAsync<long>("select count(*) from analytics.report_run where scope->>'orgId' = @o and state = 'available' and media_id is null",
            ("o", org.ToString()))).ShouldBe(4);
        (await api.ScalarAsync<int>("select suppressed_cell_count from analytics.report_run where scope->>'orgId' = @o and report_key = 'provider.participation' and format = 'csv'",
            ("o", org.ToString()))).ShouldBeGreaterThan(0);
    }
}
