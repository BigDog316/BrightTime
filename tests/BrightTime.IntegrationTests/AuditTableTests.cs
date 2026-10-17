using BrightTime.Infrastructure.Auditing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// <c>platform.audit_event</c> as the database enforces it: append-only by grant and by trigger,
/// on the parent and on every partition, with partition upkeep reserved to the jobs role.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuditTableTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    private static string CurrentPartition => "audit_event_" + DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_application_can_append_but_not_change_or_remove_a_row()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync("audit-app");

        long written = await Sql.AsAppAsync(Cs, tenant, null, async (c, t) =>
        {
            await InsertAsync(c, t, tenant);
            return await Sql.ScalarAsync<long>(c, t, "select count(*) from platform.audit_event", []);
        }, Ct);
        written.ShouldBe(1);

        foreach (string change in new[]
                 {
                     "update platform.audit_event set reason = 'edited'",
                     "delete from platform.audit_event",
                     $"delete from platform.{CurrentPartition}",
                     $"update platform.{CurrentPartition} set reason = 'edited'",
                     "delete from platform.audit_event_default",
                 })
        {
            PostgresException refused = await Should.ThrowAsync<PostgresException>(() =>
                Sql.AsAppAsync(Cs, tenant, null, async (c, t) =>
                {
                    await InsertAsync(c, t, tenant);
                    await Sql.ExecAsync(c, t, change, []);
                }, Ct));
            refused.SqlState.ShouldBe("42501", change);
        }
    }

    [Fact]
    public async Task Not_even_the_owner_can_change_a_row_by_way_of_a_partition()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync("audit-owner");

        foreach (string change in new[]
                 {
                     "update platform.audit_event set reason = 'edited' where tenant_id = @t",
                     $"update platform.{CurrentPartition} set reason = 'edited' where tenant_id = @t",
                     $"delete from platform.{CurrentPartition} where tenant_id = @t",
                     $"truncate platform.{CurrentPartition}",
                     "truncate platform.audit_event_default",
                 })
        {
            (await OwnerAttemptAsync(tenant, change)).MessageText.ShouldContain("append-only", Case.Sensitive, change);
        }
    }

    /// <summary>As the superuser, which owns everything and bypasses every grant: only the trigger stands in the way.</summary>
    private async Task<PostgresException> OwnerAttemptAsync(Guid tenant, string change)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(Ct);
        await InsertAsync(connection, tx, tenant);
        return await Should.ThrowAsync<PostgresException>(() => Sql.ExecAsync(connection, tx, change, [("t", tenant)]));
    }

    [Fact]
    public async Task Every_partition_is_sealed_however_it_was_made()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);

        long unsealed = await Sql.ScalarAsync<long>(connection, null,
            """
            select count(*) from pg_inherits i join pg_class c on c.oid = i.inhrelid
            where i.inhparent = 'platform.audit_event'::regclass
              and (not exists (select 1 from pg_trigger t where t.tgrelid = c.oid and t.tgname = 'audit_partition_append_only')
                   or has_table_privilege('brighttime_app', c.oid, 'select,insert,update,delete')
                   or has_table_privilege('brighttime_jobs', c.oid, 'select,insert,update,delete'))
            """, []);
        unsealed.ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_jobs_role_may_run_partition_upkeep()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync("audit-upkeep");

        foreach (string call in new[] { "select platform.ensure_audit_partitions(1)", "select platform.audit_default_partition_rows()", "select platform.seal_audit_partition('audit_event_default')" })
        {
            PostgresException refused = await Should.ThrowAsync<PostgresException>(() =>
                Sql.AsAppAsync(Cs, tenant, null, (c, t) => Sql.ExecAsync(c, t, call, []), Ct));
            refused.SqlState.ShouldBe("42501", call);
        }

        long stray = await Sql.AsRoleAsync(Cs, "brighttime_jobs", tenant, null, async (c, t) =>
        {
            await Sql.ExecAsync(c, t, "select platform.ensure_audit_partitions(6)", []);
            return await Sql.ScalarAsync<long>(c, t, "select platform.audit_default_partition_rows()", []);
        }, Ct);
        stray.ShouldBe(0);

        // Sealing is the owner's own step inside upkeep, not something the worker calls.
        PostgresException sealing = await Should.ThrowAsync<PostgresException>(() =>
            Sql.AsRoleAsync(Cs, "brighttime_jobs", tenant, null, async (c, t) =>
            {
                await Sql.ExecAsync(c, t, "select platform.seal_audit_partition('audit_event_default')", []);
                return true;
            }, Ct));
        sealing.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task The_worker_creates_partitions_ahead_and_reports_stray_rows()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(Cs);
        using AuditPartitionMaintenance maintenance = new(source, NullLogger<AuditPartitionMaintenance>.Instance);

        (await maintenance.RunOnceAsync(Ct)).ShouldBe(0);

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        string last = "audit_event_" + DateTime.UtcNow.AddMonths(AuditPartitionMaintenance.MonthsAhead - 1)
            .ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from pg_class where relname = @n and relnamespace = 'platform'::regnamespace", [("n", last)]))
            .ShouldBe(1, $"{last} exists a quarter ahead");
    }

    private async Task<Guid> TenantAsync(string prefix)
    {
        Guid tenant = Guid.CreateVersion7();
        await using NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(seed, tenant, prefix + "-" + tenant.ToString("N"));
        return tenant;
    }

    private static Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant) =>
        Sql.ExecAsync(connection, transaction,
            "insert into platform.audit_event (id, tenant_id, actor_kind, action) values (@id, @t, 'anonymous', 'test.append')",
            [("id", Guid.CreateVersion7()), ("t", tenant)]);
}
