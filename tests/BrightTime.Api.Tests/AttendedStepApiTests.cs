using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Enrollment.Roster;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.Modules.Learning.Badges;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Playlists;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// Attendance counted towards playlists (D91): a step to attend an activity is done by the worker
/// once the young person's attendance is recorded. In a playlist in any order that finishes it and
/// awards the badge, signed under the site's address; in an ordered one the step waits for the
/// steps before it, and is done the moment the last of them is.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the fifteenth organisation with no staff (the other API tests
/// take the third to fourteenth). The young person is a fresh adult account applying for themselves.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class AttendedStepApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Site = new($"https://{ApiFixture.Host}/");

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org) Programme()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(14).First();
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

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static string[] States(JsonElement playlist) => [.. playlist.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("state").GetString()!)];

    /// <summary>The site's address, as the jobs host builds it from the tenant's primary domain.</summary>
    private sealed class FixedSite : ISiteAddress
    {
        public Task<Uri> BaseUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult(Site);
    }

    /// <summary>
    /// The worker's handler for <see cref="AttendanceChanged"/>, on the jobs login with no person
    /// acting, as the jobs host runs it when the outbox delivers: projection, then playlists.
    /// </summary>
    private async Task DeliverAsync(Guid enrollmentId)
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
        services.AddBrightTimeDbContext<IdentityDbContext>(jobs, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);

        // The API host's cipher and vault: in Development their keys are made up per process, and
        // the worker reads names the API sealed and signs with the issuer key the API made.
        services.AddSingleton(api.Factory.Services.GetRequiredService<IFieldCipher>());
        services.AddSingleton(api.Factory.Services.GetRequiredService<IKeyVault>());
        services.AddScoped<IEnrolmentQueries, EnrolmentQueries>();
        services.AddScoped<IAttendanceQueries, AttendanceQueries>();
        services.AddScoped<IListingOccurrences, ListingOccurrences>();
        services.AddScoped<IListingDetails, SqlListingDetails>();
        services.AddScoped<IDirectoryQueries, DirectoryQueries>();
        services.AddScoped<IIdentityQueries, IdentityQueries>();
        services.AddScoped<PlatformQueries>();
        services.AddScoped<ITaxonomyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddScoped<IGeographyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddScoped(sp => new AuditTrail(sp.GetRequiredService<ITenantContext>(), NoAuditRequestInfo.Instance, sp.GetRequiredService<IClock>()));
        services.AddScoped<ISiteAddress, FixedSite>();
        services.AddScoped<ParticipationProjector>();
        services.AddScoped<Badges>();
        services.AddScoped<Playlists>();
        services.AddScoped<AttendanceChangedHandler>();
        await using ServiceProvider worker = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(api.Data.TenantId), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Locale.English);
        await scope.ServiceProvider.GetRequiredService<AttendanceChangedHandler>().HandleAsync(new AttendanceChanged(enrollmentId), Ct);
    }

    [Fact]
    public async Task Attendance_does_a_step_to_attend_finishing_a_playlist_and_in_order_waits_for_the_steps_before()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme();
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        using (admin)
        using (learner)
        {
            // A badge, and two playlists of the organisation's: one in any order, all attending; one in order, writing first.
            Guid badge = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes",
                new { name = "Regular", description = "Came along to the activity.", criteria = "Attended a session of the activity and took part." }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{badge}/publish"), null, Ct));
            Guid anyOrder = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists",
                new { title = "Turn Up " + Guid.NewGuid().ToString("N")[..6], isOrdered = false, completionBadgeClassId = badge }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{anyOrder}/steps",
                new { steps = new object[] { new { kind = "attend_listing", title = "Come to a session", listingId = listing } } }, Ct));
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{anyOrder}/publish"), null, Ct));
            Guid inOrder = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists",
                new { title = "Think Then Go " + Guid.NewGuid().ToString("N")[..6], isOrdered = true }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            JsonElement steps = (await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{inOrder}/steps", new
            {
                steps = new object[]
                {
                    new { kind = "reflection", title = "What do you hope to learn?" },
                    new { kind = "attend_listing", title = "Come to a session", listingId = listing },
                },
            }, Ct))).GetProperty("steps");
            Guid reflect = steps[0].GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{inOrder}/publish"), null, Ct));

            // The young person's own household entry, linked to their account, accepted; both playlists started.
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 1 }, Ct));
            string dob = DateTime.UtcNow.AddYears(-19).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Guid member = (await JsonAsync(await learner.PostAsJsonAsync("api/v1/family-profile/members",
                new { role = "youth", firstName = "Efua", lastName = "Darko", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = learnerId }, Ct));
            using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
            apply.Headers.Add("Idempotency-Key", "attended-" + Guid.NewGuid().ToString("N"));
            Guid application = (await JsonAsync(await learner.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await learner.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
            Guid enrolment = await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{anyOrder}/enroll"), null, Ct));
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{inOrder}/enroll"), null, Ct));

            // Nothing attended yet: delivering changes nothing.
            await DeliverAsync(enrolment);
            JsonElement[] mine = [.. (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/playlists"), Ct))).EnumerateArray()];
            mine.Select(States).SelectMany(s => s).ShouldAllBe(s => s == "not_started");

            // A meeting attended, recorded by staff, and delivered twice as a redelivery would.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset met = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-3);
            Guid session = UuidV7.NewId();
            await api.ExecuteAsync("""
                insert into opportunity.listing_session (id, tenant_id, listing_id, timezone, starts_on, start_time, end_time)
                values (@s, @t, @l, 'America/New_York', current_date - 1, '09:00', '10:00');
                insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, starts_at, ends_at, day_of_week, is_cancelled)
                values (@t, @l, @s, @met, @met + interval '1 hour', 1, false);
                """, ("s", session), ("t", api.Data.TenantId), ("l", listing), ("met", met));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/attendance", new { enrollmentId = enrolment, occurrenceStartsAt = met, state = "present" }, Ct), HttpStatusCode.Created);
            await DeliverAsync(enrolment);
            await DeliverAsync(enrolment);

            // In any order: done, the playlist finished, its badge awarded once and signed under the site's address.
            mine = [.. (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/playlists"), Ct))).EnumerateArray()];
            JsonElement finished = mine.Single(p => p.GetProperty("playlistId").GetGuid() == anyOrder);
            States(finished).ShouldBe(["completed"]);
            finished.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
            Guid assertion = finished.GetProperty("awardedAssertionId").GetGuid();
            (await api.ScalarAsync<long>("select count(*) from learning.badge_assertion where badge_class_id = @b and recipient_user_id = @u", ("b", badge), ("u", learnerId))).ShouldBe(1);
            string jwt = await api.ScalarAsync<string>("select credential_json from learning.badge_assertion where id = @a", ("a", assertion));
            jwt.ShouldContain($"https://{ApiFixture.Host}/api/v1/public/badges/classes/{badge:N}");
            (await api.ScalarAsync<long>("select count(*) from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u and payload::text like '%notify.badge.awarded%'",
                ("u", "%" + learnerId + "%"))).ShouldBe(1, "they're told of the badge, once");

            // In order: the attended step waits for the writing before it, then is done the moment that is.
            JsonElement waiting = mine.Single(p => p.GetProperty("playlistId").GetGuid() == inOrder);
            States(waiting).ShouldBe(["not_started", "not_started"]);
            JsonElement written = await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{inOrder}/steps/{reflect}",
                new { reflection = "How the orchestra tunes before a concert." }, Ct));
            States(written).ShouldBe(["completed", "completed"]);
            written.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
        }
    }
}
