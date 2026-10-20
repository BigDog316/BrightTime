using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// When the terms change (D37): a young person whose stored capabilities let them sign up for
/// programs themselves, with no agreement to the current terms, is recomputed on the worker and
/// loses it; agreeing restores it, and the sweep then finds nothing.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class TermsSweepTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task A_stale_self_enrolment_waits_for_the_current_terms_and_returns_once_they_are_agreed()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        Guid teen = UuidV7.NewId();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly birthMonth = new DateOnly(today.Year, today.Month, 1).AddYears(-15).AddMonths(-2);

        // A 15-year-old who agreed to an older version: the stored row still says yes.
        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "terms-" + tenant.ToString("N"));
            await Sql.ExecAsync(connection, null,
                """
                insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                                               security_stamp, concurrency_stamp, account_kind, status, birth_month, age_band, terms_accepted_at)
                values (@u, @t, @e, upper(@e), @e, upper(@e), true, 's', 'c', 'youth', 'active', @bm, '15_17', now() - interval '1 year');
                insert into identity.consent_record (id, tenant_id, user_id, document_kind, document_version, document_hash)
                values (gen_random_uuid(), @t, @u, 'terms', 'older', '\x00'::bytea);
                insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_self_enroll)
                values (@u, @t, false, true, true);
                """,
                [("t", tenant), ("u", teen), ("e", $"teen-{teen:N}@example.org"), ("bm", birthMonth)]);
        }

        string queue = "terms-" + teen.ToString("N")[..8];
        await using ServiceProvider worker = TestHost.Worker(Cs, queue, services =>
        {
            string jobs = TestHost.As(Cs, TestHost.JobsLogin);
            services.AddBrightTimeDbContext<IdentityDbContext>(jobs, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
            services.AddScoped(sp => new AuditTrail(
                sp.GetRequiredService<Infrastructure.Tenancy.ITenantContext>(), NoAuditRequestInfo.Instance, sp.GetRequiredService<IClock>(), sp.GetRequiredService<NpgsqlDataSource>()));
            services.AddSingleton<IAuditRequestInfo>(NoAuditRequestInfo.Instance);
            services.AddGuardianshipJobs();
        });

        (await SweepAsync(worker, tenant)).ShouldBe(1);
        (await SweepAsync(worker, tenant)).ShouldBe(1, "found again until the worker runs, but booked once");
        string key = $"{RecomputeCapabilitiesHandler.JobType}:{teen:N}:terms-{AccountTerms.Current.HashHex[..12]}";
        await using NpgsqlConnection check = await Sql.OpenAsync(Cs, Ct);
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.job where unique_key = @k", [("k", key)])).ShouldBe(1);

        // The worker recomputes: no agreement to these terms, no self-enrolment.
        await Sql.ExecAsync(check, null, "update platform.job set queue = @q where unique_key = @k", [("q", queue), ("k", key)]);
        await TestHost.DrainAsync(worker, Ct);
        (await Sql.ScalarAsync<bool>(check, null, "select can_self_enroll from identity.youth_capability where user_id = @u", [("u", teen)])).ShouldBeFalse();
        (await SweepAsync(worker, tenant)).ShouldBe(0);

        // Agreeing to the current terms brings it back, and leaves nothing stale.
        await Sql.ExecAsync(check, null,
            "insert into identity.consent_record (id, tenant_id, user_id, document_kind, document_version, document_hash) values (gen_random_uuid(), @t, @u, 'terms', @v, @h)",
            [("t", tenant), ("u", teen), ("v", AccountTerms.Current.Version), ("h", AccountTerms.Current.Hash)]);
        await using (AsyncServiceScope scope = worker.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<Infrastructure.Tenancy.TenantContext>()
                .SetTenant(new TenantId(tenant), TimeZoneInfo.Utc, SharedKernel.Primitives.Locale.English);
            (await scope.ServiceProvider.GetRequiredService<Guardianship>().RecomputeAsync(new UserId(teen), Ct)).CanSelfEnroll.ShouldBeTrue();
        }

        (await SweepAsync(worker, tenant)).ShouldBe(0);
    }

    private static async Task<int> SweepAsync(ServiceProvider worker, Guid tenant)
    {
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        return await TermsSweep.SweepAsync(scope.ServiceProvider, new TenantId(tenant), Ct);
    }
}
