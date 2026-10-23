using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Enrollment.Roster;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// The learner portfolio (M4, D65): a young member linked to their own account, their attendance
/// projected into participation on the worker, a portfolio they hide records in and choose to show,
/// and the page anyone with its link sees.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the ninth organisation with no staff (M3AcceptanceTests takes
/// the seventh and eighth); its admin accepts and records attendance. The young person is a fresh
/// adult account (18 or over) applying for themselves.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class PortfolioApiTests(ApiFixture api)
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
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(8).First();
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    /// <summary>The projector, on the worker's login, as the jobs host runs it when the outbox delivers.</summary>
    private async Task ProjectAsync(Guid enrollmentId)
    {
        string jobs = api.JobsConnection;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddBrightTimeDbContext<LearningDbContext>(jobs, LearningDbContext.SchemaName, LearningDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<EnrollmentDbContext>(jobs, EnrollmentDbContext.SchemaName, EnrollmentDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<AttendanceDbContext>(jobs, AttendanceDbContext.SchemaName, AttendanceDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<OpportunitiesDbContext>(jobs, OpportunitiesDbContext.SchemaName, OpportunitiesDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<DirectoryDbContext>(jobs, DirectoryDbContext.SchemaName, DirectoryDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<PlatformDbContext>(jobs, PlatformDbContext.SchemaName, PlatformDbContext.MigrationsHistoryTable);
        services.AddFieldEncryption(new ConfigurationBuilder().Build(), development: true);
        services.AddScoped<IEnrolmentQueries, EnrolmentQueries>();
        services.AddScoped<IAttendanceQueries, AttendanceQueries>();
        services.AddScoped<IListingOccurrences, ListingOccurrences>();
        services.AddScoped<IListingDetails, SqlListingDetails>();
        services.AddScoped<IDirectoryQueries, DirectoryQueries>();
        services.AddScoped<PlatformQueries>();
        services.AddScoped<ITaxonomyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddScoped<IGeographyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddScoped<ParticipationProjector>();
        await using ServiceProvider worker = services.BuildServiceProvider();
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(api.Data.TenantId), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Locale.English);
        await scope.ServiceProvider.GetRequiredService<ParticipationProjector>().ProjectAsync(enrollmentId, Ct);
    }

    private Task<long> OutboxAsync(string type, Guid enrollmentId) =>
        api.ScalarAsync<long>("select count(*) from platform.outbox_message where message_type = @t and payload->>'enrollmentId' = @e",
            ("t", type), ("e", enrollmentId.ToString()));

    [Fact]
    public async Task Attendance_becomes_the_young_persons_portfolio_which_they_hide_from_and_choose_to_show()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme();
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        string email = await api.NewAdultAsync();
        (HttpClient learner, Guid learnerId) = await SignedInAsync(email);
        (HttpClient someoneElse, Guid otherId) = await SignedInAsync(await api.NewAdultAsync());
        using (admin)
        using (learner)
        using (someoneElse)
        {
            // The young person's own household entry, applied for and accepted.
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 1 }, Ct));
            string dob = DateTime.UtcNow.AddYears(-19).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Guid member = (await JsonAsync(await learner.PostAsJsonAsync("api/v1/family-profile/members",
                new { role = "youth", firstName = "Kwesi", lastName = "Ofori", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            using HttpRequestMessage apply = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
            apply.Headers.Add("Idempotency-Key", "portfolio-" + Guid.NewGuid().ToString("N"));
            Guid application = (await JsonAsync(await learner.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await learner.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
            Guid enrolment = await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));

            // Linked to their own account; another adult's account can't be claimed, nor can an adult member.
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = otherId }, Ct), HttpStatusCode.Forbidden, "family.link_forbidden");
            (await JsonAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = learnerId }, Ct)))
                .GetProperty("accountUserId").GetGuid().ShouldBe(learnerId);
            (await api.ScalarAsync<Guid>("select youth_user_id from enrollment.enrollment where id = @e", ("e", enrolment))).ShouldBe(learnerId);
            (await OutboxAsync(EnrolmentAccountChanged.MessageType, enrolment)).ShouldBe(1);

            // Two meetings: one attended whole (an hour), one late for 45 minutes. Each record is published.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset second = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-3);
            DateTimeOffset first = second.AddDays(-2);
            Guid session = UuidV7.NewId();
            await api.ExecuteAsync("""
                insert into opportunity.listing_session (id, tenant_id, listing_id, timezone, starts_on, start_time, end_time)
                values (@s, @t, @l, 'America/New_York', current_date - 3, '09:00', '10:00');
                insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, starts_at, ends_at, day_of_week, is_cancelled)
                values (@t, @l, @s, @first, @first + interval '1 hour', 1, false),
                       (@t, @l, @s, @second, @second + interval '1 hour', 1, false);
                """, ("s", session), ("t", api.Data.TenantId), ("l", listing), ("first", first), ("second", second));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/attendance", new { enrollmentId = enrolment, occurrenceStartsAt = first, state = "present" }, Ct), HttpStatusCode.Created);
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/attendance", new { enrollmentId = enrolment, occurrenceStartsAt = second, state = "late", minutesAttended = 45 }, Ct), HttpStatusCode.Created);
            (await OutboxAsync(AttendanceChanged.MessageType, enrolment)).ShouldBe(2);

            // The worker projects it, twice over as a redelivery would, into one record.
            await ProjectAsync(enrolment);
            await ProjectAsync(enrolment);
            JsonElement mine = await JsonAsync(await learner.GetAsync(new Uri("api/v1/me/portfolio", UriKind.Relative), Ct));
            JsonElement record = mine.GetProperty("participation").EnumerateArray().Single();
            (record.GetProperty("title").GetString(), record.GetProperty("sessionsAttended").GetInt32(), record.GetProperty("hours").GetDecimal(), record.GetProperty("source").GetString())
                .ShouldBe((api.Data.Listings.Single(l => l.Id == listing).Title, 2, 1.75m, "attendance"));
            record.GetProperty("organisation").GetString().ShouldBe(api.Data.Organisations.Single(o => o.Id == org).DisplayName);
            (mine.GetProperty("visibility").GetString(), mine.GetProperty("totalHours").GetDecimal(), mine.GetProperty("canShow").GetBoolean()).ShouldBe(("private", 1.75m, true));
            Guid participation = record.GetProperty("id").GetGuid();

            // Hidden, the hours leave the total; shown again, they come back.
            (await JsonAsync(await learner.PostAsync(new Uri($"api/v1/me/portfolio/participation/{participation}/hide", UriKind.Relative), null, Ct))).GetProperty("totalHours").GetDecimal().ShouldBe(0m);
            (await JsonAsync(await learner.PostAsync(new Uri($"api/v1/me/portfolio/participation/{participation}/show", UriKind.Relative), null, Ct))).GetProperty("totalHours").GetDecimal().ShouldBe(1.75m);
            await ProblemAsync(await someoneElse.PostAsync(new Uri($"api/v1/me/portfolio/participation/{participation}/hide", UriKind.Relative), null, Ct), HttpStatusCode.NotFound, "portfolio.not_found");

            // Shown only with a link name and a display name without the family name.
            string slug = "kwesi-" + Guid.NewGuid().ToString("N")[..8];
            using HttpClient anonymous = api.Client();
            (await anonymous.GetAsync(new Uri($"api/v1/public/portfolios/{slug}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            await ProblemAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Kwesi", visibility = "link_only" }, Ct), HttpStatusCode.BadRequest, "portfolio.slug");
            await ProblemAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Kwesi Ofori", visibility = "link_only", publicSlug = slug }, Ct),
                HttpStatusCode.BadRequest, "portfolio.display_name_legal");
            (await JsonAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Kwesi Builds", headline = "Robots and rivers", visibility = "link_only", publicSlug = slug }, Ct)))
                .GetProperty("visibility").GetString().ShouldBe("link_only");
            await ProblemAsync(await someoneElse.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Another", visibility = "public", publicSlug = slug }, Ct),
                HttpStatusCode.Conflict, "portfolio.slug_taken");

            // Anyone with the link sees the display name and the record, never found by a search engine.
            HttpResponseMessage shown = await anonymous.GetAsync(new Uri($"api/v1/public/portfolios/{slug.ToUpperInvariant()}", UriKind.Relative), Ct);
            JsonElement page = await JsonAsync(shown);
            shown.Headers.GetValues("X-Robots-Tag").Single().ShouldContain("noindex");
            (page.GetProperty("displayName").GetString(), page.GetProperty("listed").GetBoolean(), page.GetProperty("totalHours").GetDecimal()).ShouldBe(("Kwesi Builds", false, 1.75m));
            page.GetProperty("participation").EnumerateArray().Single().GetProperty("sessionsAttended").GetInt32().ShouldBe(2);
            string raw = page.GetRawText();
            raw.ShouldNotContain("Ofori");
            raw.ShouldNotContain(learnerId.ToString());
            raw.ShouldNotContain(enrolment.ToString());

            // A hidden record leaves the shown page too; private again, the page is gone.
            await learner.PostAsync(new Uri($"api/v1/me/portfolio/participation/{participation}/hide", UriKind.Relative), null, Ct);
            (await JsonAsync(await anonymous.GetAsync(new Uri($"api/v1/public/portfolios/{slug}", UriKind.Relative), Ct))).GetProperty("participation").GetArrayLength().ShouldBe(0);
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Kwesi Builds", visibility = "private", publicSlug = slug }, Ct));
            await ProblemAsync(await anonymous.GetAsync(new Uri($"api/v1/public/portfolios/{slug}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "portfolio.not_shown");

            // Without the showcase capability, nothing is shown at all.
            await api.ExecuteAsync("""
                insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_publish_showcase, recomputed_at)
                values (@u, @t, false, true, false, now())
                on conflict (user_id) do update set can_publish_showcase = false
                """, ("u", otherId), ("t", api.Data.TenantId));
            await ProblemAsync(await someoneElse.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Another", visibility = "public", publicSlug = "another-" + Guid.NewGuid().ToString("N")[..8] }, Ct),
                HttpStatusCode.Forbidden, "portfolio.showcase");

            // Unlinked, the enrolment leaves the young person's portfolio.
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = (Guid?)null }, Ct));
            await ProjectAsync(enrolment);
            (await JsonAsync(await learner.GetAsync(new Uri("api/v1/me/portfolio", UriKind.Relative), Ct))).GetProperty("participation").GetArrayLength().ShouldBe(0);
        }
    }
}
