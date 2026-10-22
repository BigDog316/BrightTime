using System.Globalization;
using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using DecisionService = BrightTime.Modules.Enrollment.Decisions.Decisions;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's attendance (D60): a week of meetings, a meeting's roster, marks saved
/// and corrected with a reason, the corrections shown, and who may see any of it.
/// </summary>
/// <remarks>
/// The provider is the second approved organisation from sorted index 52 on with a published
/// builtin listing (ApplicationPagesTests takes the first). The family is the admin account at
/// index 51 and the stranger the admin at index 50, as there; the tests run one after another.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class AttendancePagesTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private AsyncServiceScope As(Guid user)
    {
        AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        TenantContext tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(new TenantId(db.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
        tenant.SetActor(new UserId(user), ActorKind.User);
        return scope;
    }

    /// <summary>Each child applies with their name shared and the organisation accepts them, as the family's and the staff's own requests would.</summary>
    private async Task EnrolAsync(Guid family, Guid staff, SeedOrganisation org, Guid listing, params string[] children)
    {
        List<Guid> applied = [];
        await using (AsyncServiceScope scope = As(family))
        {
            FamilyProfiles profiles = scope.ServiceProvider.GetRequiredService<FamilyProfiles>();
            Applications applications = scope.ServiceProvider.GetRequiredService<Applications>();
            (await profiles.SaveAsync(new UserId(family), new FamilyInput(4, "en", false), Ct)).IsSuccess.ShouldBeTrue();
            foreach (string child in children)
            {
                Result<MemberView> member = await profiles.AddMemberAsync(new UserId(family), new MemberInput("youth", child, "Mensah", null, new DateOnly(2014, 2, 2), 6, null, null), Ct);
                var created = await applications.CreateAsync(new UserId(family), listing, member.Value.Id, "att-" + Guid.NewGuid().ToString("N"), Ct);
                (await applications.SubmitAsync(new UserId(family), created.Value.View.Id, new SubmitInput(["member_names"], null), Ct)).IsSuccess.ShouldBeTrue();
                applied.Add(created.Value.View.Id);
            }
        }

        await using (AsyncServiceScope scope = As(staff))
        {
            DecisionService decisions = scope.ServiceProvider.GetRequiredService<DecisionService>();
            (await decisions.SetSeatsAsync(new OrganisationId(org.Id), listing, 20, null, Ct)).IsSuccess.ShouldBeTrue();
            foreach (Guid application in applied)
            {
                var accepted = await decisions.DecideAsync(new OrganisationId(org.Id), new UserId(staff), application, DecisionService.Accept, null, Ct);
                accepted.IsSuccess.ShouldBeTrue(accepted.IsSuccess ? null : accepted.Error.Code);
            }
        }
    }

    [Fact]
    public async Task Staff_mark_a_meeting_correct_a_mark_with_a_reason_and_nobody_else_sees_it()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(1).First();
        Guid listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id;
        Guid staffId = SeedIds.For("user/" + AdminOf(provider));
        await EnrolAsync(SeedIds.For("user/" + AdminOf(sorted[51])), staffId, provider, listing, "Abena", "Yaw");

        // A meeting that started an hour ago.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset starts = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-1);
        Guid session = UuidV7.NewId();
        await db.ScalarAsync<int>("""
            insert into opportunity.listing_session (id, tenant_id, listing_id, timezone, starts_on, start_time, end_time)
            values (@s, @t, @l, 'America/New_York', current_date - 1, '09:00', '10:00');
            insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, starts_at, ends_at, day_of_week, is_cancelled)
            values (@t, @l, @s, @at, @at + interval '1 hour', 1, false);
            select 1;
            """, ("s", session), ("t", db.Data.TenantId), ("l", listing), ("at", starts));
        string roster = $"/provider/orgs/{provider.Id}/listings/{listing}/roster?at={Uri.EscapeDataString(starts.ToString("o", CultureInfo.InvariantCulture))}";

        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));

        // The week's meetings lead to the roster, which names both children.
        string week = $"/provider/orgs/{provider.Id}/attendance?from={starts.AddDays(-1):yyyy-MM-dd}";
        (await staff.GetAsync(week)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain(WebUtility.HtmlEncode(roster));
        (await staff.GetAsync(roster)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain("Abena M.");
        staff.Html.ShouldContain("Yaw M.");
        string[] ids = [.. System.Text.RegularExpressions.Regex.Matches(staff.Html, "name=\"(mark-[0-9a-f]{32})\"").Select(m => m.Groups[1].Value)];
        ids.Length.ShouldBe(2);

        // Both marked; then one changed without a reason (refused), then with one (corrected).
        HttpResponseMessage saved = await staff.PostAsync(roster, "marks", (ids[0], "present"), (ids[1], "absent"), ("reason", ""));
        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(saved.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Attendance saved.");
        (await db.ScalarAsync<long>("select count(*) from attendance.attendance_record where listing_id = @l and occurrence_starts_at = @at and superseded_by is null",
            ("l", listing), ("at", starts))).ShouldBe(2);

        (await staff.PostAsync(roster, "marks", (ids[0], "present"), (ids[1], "late"), ("reason", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain("Say why you&#x27;re changing a mark that was already saved.");
        HttpResponseMessage corrected = await staff.PostAsync(roster, "marks", (ids[0], "present"), (ids[1], "late"), ("reason", "Arrived after the register"));
        corrected.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(corrected.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Corrected marks");
        staff.Html.ShouldContain("Absent (");
        (await db.ScalarAsync<long>("select count(*) from attendance.attendance_record where listing_id = @l and occurrence_starts_at = @at", ("l", listing), ("at", starts))).ShouldBe(3);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'attendance.corrected' and actor_org_id = @o and detail->>'reason' = 'Arrived after the register'",
            ("o", provider.Id))).ShouldBe(1);

        // Another organisation's admin sees neither the week nor the roster.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(week)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.GetAsync(roster)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("Abena");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'attendance.roster.read' and outcome = 'deny' and actor_org_id = @o",
            ("o", provider.Id))).ShouldBeGreaterThanOrEqualTo(1);
    }
}
