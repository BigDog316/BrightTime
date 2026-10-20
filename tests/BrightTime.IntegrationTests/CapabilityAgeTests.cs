using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The recompute booked for the day a child's age band changes, run by the worker as the jobs
/// host runs it, on the jobs login.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityAgeTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task On_the_day_a_child_turns_13_the_band_moves_the_floor_holds_and_the_next_day_is_booked()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        Guid guardian = UuidV7.NewId();
        Guid child = UuidV7.NewId();
        Guid link = UuidV7.NewId();

        // Born in a month thirteen years and two months ago: 13 by any reading of the month.
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly birthMonth = new DateOnly(today.Year, today.Month, 1).AddYears(-13).AddMonths(-2);

        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "age-" + tenant.ToString("N"));
            // One statement list, so the deferred child-consent trigger sees the consent row.
            await Sql.ExecAsync(connection, null,
                """
                insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                                               security_stamp, concurrency_stamp, account_kind, status)
                values (@g, @t, @ge, upper(@ge), @ge, upper(@ge), true, 's', 'c', 'adult', 'active');
                insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, security_stamp, concurrency_stamp,
                                               account_kind, status, birth_month, age_band)
                values (@c, @t, @cn, upper(@cn), 's', 'c', 'child', 'active', @bm, '9_12');
                insert into identity.guardian_link (id, tenant_id, guardian_user_id, child_user_id, relationship, status,
                                                    verification_method, verified_at, is_primary)
                values (@l, @t, @g, @c, 'parent', 'verified', 'signed_form_review', now(), true);
                insert into identity.parental_consent (id, tenant_id, child_user_id, guardian_user_id, guardian_link_id, scope, method, consent_text_hash)
                values (gen_random_uuid(), @t, @c, @g, @l, 'account', 'signed_form_review', '\x00'::bytea);
                insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent)
                values (@c, @t, true, true);
                """,
                [("t", tenant), ("g", guardian), ("ge", $"guardian-{guardian:N}@example.org"), ("c", child),
                 ("cn", $"child-{child:N}"), ("bm", birthMonth), ("l", link)]);
        }

        string queue = "age-" + child.ToString("N")[..8];
        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.ExecAsync(connection, null,
                """
                insert into platform.job (id, tenant_id, queue, job_type, payload, run_after)
                values (gen_random_uuid(), @t, @q, @type, jsonb_build_object('userId', @c::text), now())
                """,
                [("t", tenant), ("q", queue), ("type", RecomputeCapabilitiesHandler.JobType), ("c", child)]);
        }

        await using ServiceProvider worker = TestHost.Worker(Cs, queue, services =>
        {
            string jobs = TestHost.As(Cs, TestHost.JobsLogin);
            services.AddBrightTimeDbContext<IdentityDbContext>(jobs, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
            services.AddScoped(sp => new AuditTrail(
                sp.GetRequiredService<ITenantContext>(), NoAuditRequestInfo.Instance, sp.GetRequiredService<IClock>(), sp.GetRequiredService<NpgsqlDataSource>()));
            services.AddSingleton<IAuditRequestInfo>(NoAuditRequestInfo.Instance);
            services.AddGuardianshipJobs();
        });
        await TestHost.DrainAsync(worker, Ct);

        await using NpgsqlConnection check = await Sql.OpenAsync(Cs, Ct);
        (await Sql.ScalarAsync<string>(check, null, "select age_band from identity.app_user where id = @c", [("c", child)])).ShouldBe("13_14");

        // A child profile keeps the floor: protections hold until the young person has their own sign-in.
        (await Sql.ScalarAsync<bool>(check, null, "select is_under_13 from identity.youth_capability where user_id = @c", [("c", child)])).ShouldBeTrue();

        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.audit_event where action = @a and subject_ids = array[@c]", [("a", Guardianship.Turned13), ("c", child)])).ShouldBe(1);

        // The next change, at 15, is booked: on the anniversary of the month's last day.
        DateOnly fifteen = birthMonth.AddMonths(1).AddDays(-1).AddYears(15);
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.job where unique_key = @k and state = 'ready' and run_after > now()", [("k", $"{RecomputeCapabilitiesHandler.JobType}:{child:N}:{fifteen:yyyy-MM-dd}")])).ShouldBe(1);
    }
}
