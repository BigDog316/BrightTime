using BrightTime.Infrastructure.Tenancy;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The M1 cross-tenant matrix: every module table, every application role.
/// </summary>
/// <remarks>
/// <para>
/// Two halves. The catalogue half holds for every table, populated or not: row-level security
/// is on and forced, and the tenant policy is the one predicate, for reads and writes, for
/// every application role. The behaviour half runs against a database holding the Kettle seed
/// and a second, empty tenant: as each role, in each context, every populated table shows
/// Kettle's rows to Kettle and none to anyone else, and the other tenant's updates and deletes
/// touch nothing.
/// </para>
/// <para>
/// A table added to a module schema is covered without being named here. Exceptions are named,
/// with the reason: <c>platform.tenant</c> shows a request its own row, and
/// <c>platform.blind_index_key</c> holds the platform's key ids, which are not tenant data, and
/// <c>platform.data_protection_key</c> the hosts' key ring, encrypted, which is not either.
/// The finder's role is covered by <see cref="FinderRoleTests"/>: it bypasses RLS on one
/// table by design (D13), and its isolation is the query builder's tenant predicate.
/// </para>
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class TenantIsolationMatrixTests(DatabaseFixture database)
{
    private const string DatabaseName = "brighttime_matrixtest";

    private static readonly string[] ModuleSchemas =
        ["platform", "identity", "directory", "opportunity", "enrollment", "attendance", "learning",
         "community", "workforce", "landscape", "analytics", "notify", "integration"];

    /// <summary>The roles the application's hosts log in as, and the login used for each here.</summary>
    private static readonly (string Role, string Login)[] Roles =
    [
        ("brighttime_app", TestHost.AppLogin),
        ("brighttime_jobs", TestHost.JobsLogin),
        ("brighttime_readonly", "bt_readonly_login"),
        ("brighttime_auth", "bt_auth_login"),
    ];

    private static readonly string[] Global = ["platform.blind_index_key", "platform.data_protection_key"];

    /// <summary>
    /// Policies that deliberately reach past the tenant, with the reason, the roles they may
    /// name and the commands they may cover. Anything else permissive beside the tenant policy fails.
    /// </summary>
    private static readonly Dictionary<(string Table, string Policy), (string Why, string[] Roles, string Command)> Widening = new()
    {
        [("platform.tenant_domain", "tenant_domain_lookup")] =
            ("the tenant is found from the request's host before it is known (D18); host names are public", ["brighttime_app", "brighttime_jobs", "brighttime_auth"], "r"),
        [("platform.job", "job_worker_claims")] =
            ("the worker claims every tenant's jobs and sets each job's tenant itself (D2); payloads carry ids only", ["brighttime_jobs"], "*"),
        [("platform.outbox_message", "outbox_dispatch")] =
            ("the dispatcher fans every tenant's messages out to jobs that carry the message's tenant (D27)", ["brighttime_jobs"], "*"),
    };

    /// <summary>Tables with no actor guard over them, whose rows the application must see all of under its tenant.</summary>
    private static readonly string[] ExactlyOwn =
    [
        "directory.organisation", "directory.site", "directory.org_membership", "opportunity.listing",
        "opportunity.listing_site", "opportunity.listing_term", "opportunity.listing_search", "platform.taxonomy_term",
        "platform.taxonomy_term_name", "platform.neighbourhood",
    ];

    private static readonly Guid OtherTenant = Guid.Parse("0196c3e8-0000-7000-8000-00000000b0b0");

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record TableInfo(string Schema, string Name, bool HasTenant, bool Rls, bool Forced, bool ActorGuarded)
    {
        public string Qualified => $"{Schema}.{Name}";
    }

    // ---- catalogue -----------------------------------------------------------------------

    [Fact]
    public async Task Every_module_table_has_forced_row_level_security_and_a_tenant_policy_for_every_role()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);

        List<string> problems = [];
        foreach (TableInfo t in await TablesAsync(connection))
        {
            if (Global.Contains(t.Qualified))
            {
                continue;
            }

            if (!t.Rls || !t.Forced)
            {
                problems.Add($"{t.Qualified}: row-level security is {(t.Rls ? "on but not forced" : "off")}");
                continue;
            }

            string policy = t.HasTenant ? "tenant_isolation" : t.Qualified == "platform.tenant" ? "tenant_self" : "tenant_isolation_via_parent";
            var row = await PolicyAsync(connection, t, policy);
            if (row is null)
            {
                problems.Add($"{t.Qualified}: no {policy} policy");
                continue;
            }

            (bool permissive, string command, string[] roles, string? usingExpr, string? checkExpr) = row.Value;
            string[] missing = [.. Roles.Select(r => r.Role).Where(r => !roles.Contains(r))];
            if (missing.Length > 0)
            {
                problems.Add($"{t.Qualified}: {policy} does not apply to {string.Join(", ", missing)}");
            }

            if (!permissive || command != "*")
            {
                problems.Add($"{t.Qualified}: {policy} is {(permissive ? "" : "restrictive, ")}for {command}, not a permissive policy for all commands");
            }

            if (t.HasTenant)
            {
                const string expected = "(tenant_id = platform.current_tenant_id())";
                if (usingExpr != expected || checkExpr != expected)
                {
                    problems.Add($"{t.Qualified}: {policy} reads {usingExpr} / {checkExpr}, not {expected} for both");
                }
            }
            else if (usingExpr is null)
            {
                problems.Add($"{t.Qualified}: {policy} has no read predicate");
            }

            // A second permissive policy is OR-ed with the first and widens what a role sees,
            // unless it says no more than the tenant policy does.
            foreach (string other in await OtherPermissivePoliciesAsync(connection, t, policy))
            {
                var o = (await PolicyAsync(connection, t, other))!.Value;
                const string tenantOnly = "(tenant_id = platform.current_tenant_id())";
                if (o.Using == tenantOnly && o.Check is null or tenantOnly)
                {
                    continue;
                }

                if (Widening.TryGetValue((t.Qualified, other), out var allowed))
                {
                    if (o.Roles.Except(allowed.Roles).Any() || o.Command != allowed.Command)
                    {
                        problems.Add($"{t.Qualified}: {other} is allowed for {string.Join(", ", allowed.Roles)} ({allowed.Command}) only, because {allowed.Why}; it covers {string.Join(", ", o.Roles)} ({o.Command})");
                    }

                    continue;
                }

                problems.Add($"{t.Qualified}: a second permissive policy, {other}, widens the tenant policy");
            }
        }

        problems.ShouldBeEmpty();
    }

    // ---- behaviour -----------------------------------------------------------------------

    [Fact]
    public async Task Each_role_sees_only_its_tenants_rows_in_every_populated_table()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        string connection = await SeedOnceAsync();

        await using NpgsqlConnection admin = await Sql.OpenAsync(connection, Ct);
        List<(TableInfo Table, long Rows)> populated = [];
        foreach (TableInfo t in await TablesAsync(admin))
        {
            long rows = await Sql.ScalarAsync<long>(admin, null, $"select count(*) from {t.Qualified}", []);
            if (rows > 0 && !Global.Contains(t.Qualified))
            {
                populated.Add((t, rows));
            }
        }

        // The seed reaches every module M1 builds; a table that should have rows and does not is
        // a hole in the test, not a pass.
        string[] schemasWithRows = [.. populated.Select(p => p.Table.Schema).Distinct()];
        new[] { "directory", "identity", "opportunity", "platform" }.ShouldBeSubsetOf(schemasWithRows);
        populated.Count.ShouldBeGreaterThan(25);

        List<string> problems = [];
        foreach ((string role, string login) in Roles)
        {
            await using NpgsqlConnection as_ = await Sql.OpenAsync(TestHost.As(connection, login), Ct);
            foreach ((TableInfo t, long rows) in populated)
            {
                if (!await CanAsync(admin, role, t, "select"))
                {
                    continue;
                }

                long own = t.Qualified == "platform.tenant" ? 1 : rows;
                bool widenedRead = Widened(t, role, "r");

                if (!widenedRead)
                {
                    long other = await CountAsync(as_, t, OtherTenant);
                    long none = await CountAsync(as_, t, null);
                    if (other > (t.Qualified == "platform.tenant" ? 1 : 0))
                    {
                        problems.Add($"{role} {t.Qualified}: another tenant sees {other} of Kettle's rows");
                    }

                    if (none > 0)
                    {
                        problems.Add($"{role} {t.Qualified}: a request with no tenant sees {none} rows");
                    }
                }

                // Never more than the tenant's own rows. Fewer is allowed: the actor guard (layer 3)
                // hides youth records, and rows that hang off them, from a request with no actor.
                long kettle = await CountAsync(as_, t, KettleSeed.TenantId);
                if (!widenedRead && kettle > own)
                {
                    problems.Add($"{role} {t.Qualified}: Kettle sees {kettle} rows, more than its {own}");
                }

                if (role == "brighttime_app" && ExactlyOwn.Contains(t.Qualified) && kettle != own)
                {
                    problems.Add($"{role} {t.Qualified}: Kettle sees {kettle} of its {own} rows");
                }

                foreach (string write in new[] { "update", "delete" })
                {
                    if (Widened(t, role, write == "update" ? "w" : "d") || !await CanAsync(admin, role, t, write))
                    {
                        continue;
                    }

                    int allowedOwn = t.Qualified == "platform.tenant" ? 1 : 0;
                    if (await WriteAsOtherAsync(as_, t, write) is { } touched && touched > allowedOwn)
                    {
                        problems.Add($"{role} {t.Qualified}: another tenant's {write} touched {touched} of Kettle's rows");
                    }
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<string> SeedOnceAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_seeded is null)
            {
                (string connection, SeedData _, SeedCounts _) = await KettleDatabase.CreateAsync(database.ConnectionString, DatabaseName, Ct);
                await using NpgsqlConnection admin = await Sql.OpenAsync(connection, Ct);
                await Sql.SeedTenantAsync(admin, OtherTenant, "matrix-other");
                await Sql.ExecAsync(admin, null,
                    """
                    do $$ begin
                      if not exists (select from pg_roles where rolname = 'bt_readonly_login') then
                        create role bt_readonly_login login password 'bt-test-only' in role brighttime_readonly;
                      end if;
                      if not exists (select from pg_roles where rolname = 'bt_auth_login') then
                        create role bt_auth_login login password 'bt-test-only';
                      end if;
                      grant brighttime_auth to bt_auth_login;
                    end $$;
                    """, []);
                _seeded = connection;
            }

            return _seeded;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<List<TableInfo>> TablesAsync(NpgsqlConnection connection)
    {
        List<TableInfo> tables = [];
        await using NpgsqlCommand command = new(
            """
            select n.nspname, c.relname,
                   exists (select 1 from pg_attribute a where a.attrelid = c.oid and a.attname = 'tenant_id' and a.attnum > 0 and not a.attisdropped),
                   c.relrowsecurity, c.relforcerowsecurity,
                   exists (select 1 from pg_policy p where p.polrelid = c.oid and not p.polpermissive)
              from pg_class c
              join pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p') and not c.relispartition and n.nspname = any(@schemas)
             order by 1, 2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("schemas", ModuleSchemas));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }

        return tables;
    }

    private static async Task<(bool Permissive, string Command, string[] Roles, string? Using, string? Check)?> PolicyAsync(
        NpgsqlConnection connection, TableInfo t, string policy)
    {
        await using NpgsqlCommand command = new(
            """
            select p.polpermissive, p.polcmd::text,
                   array(select r.rolname::text from pg_roles r where r.oid = any(p.polroles)),
                   pg_get_expr(p.polqual, p.polrelid), pg_get_expr(p.polwithcheck, p.polrelid)
              from pg_policy p
             where p.polrelid = (quote_ident(@s) || '.' || quote_ident(@t))::regclass and p.polname = @p
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("s", t.Schema));
        command.Parameters.Add(new NpgsqlParameter("t", t.Name));
        command.Parameters.Add(new NpgsqlParameter("p", policy));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            return null;
        }

        string[] roles = await reader.GetFieldValueAsync<string[]>(2, Ct);
        string? usingExpr = await reader.IsDBNullAsync(3, Ct) ? null : reader.GetString(3);
        string? checkExpr = await reader.IsDBNullAsync(4, Ct) ? null : reader.GetString(4);
        return (reader.GetBoolean(0), reader.GetString(1), roles, usingExpr, checkExpr);
    }

    private static async Task<List<string>> OtherPermissivePoliciesAsync(NpgsqlConnection connection, TableInfo t, string policy)
    {
        List<string> others = [];
        await using NpgsqlCommand command = new(
            """
            select p.polname from pg_policy p
             where p.polrelid = (quote_ident(@s) || '.' || quote_ident(@t))::regclass and p.polpermissive and p.polname <> @p
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("s", t.Schema));
        command.Parameters.Add(new NpgsqlParameter("t", t.Name));
        command.Parameters.Add(new NpgsqlParameter("p", policy));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            others.Add(reader.GetString(0));
        }

        return others;
    }

    /// <summary>Whether a named widening covers this role and command ("r" read, "w" update, "d" delete).</summary>
    private static bool Widened(TableInfo t, string role, string command) =>
        Widening.Any(w => w.Key.Table == t.Qualified && w.Value.Roles.Contains(role) && (w.Value.Command == "*" || w.Value.Command == command));

    private static Task<bool> CanAsync(NpgsqlConnection admin, string role, TableInfo t, string privilege) =>
        Sql.ScalarAsync<bool>(admin, null, "select has_table_privilege(@r, @t, @p)", [("r", role), ("t", t.Qualified), ("p", privilege)]);

    /// <summary>The rows a role sees in a table, inside a transaction with the tenant set as the application sets it, or with none.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table names come from the catalogue of the test's own database.")]
    private static async Task<long> CountAsync(NpgsqlConnection connection, TableInfo t, Guid? tenant)
    {
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(Ct);
        if (tenant is { } id)
        {
            await Sql.ExecAsync(connection, tx, "select set_config(@k, @v, true)", [("k", SessionVariables.TenantId), ("v", id.ToString())]);
        }

        long rows = await Sql.ScalarAsync<long>(connection, tx, $"select count(*) from {t.Qualified}", []);
        await tx.RollbackAsync(Ct);
        return rows;
    }

    /// <summary>A no-op update, or a delete, of every row as the other tenant: how many rows it reached. Always rolled back.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table and column names come from the catalogue of the test's own database.")]
    private static async Task<int?> WriteAsOtherAsync(NpgsqlConnection connection, TableInfo t, string write)
    {
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(Ct);
        await Sql.ExecAsync(connection, tx, "select set_config(@k, @v, true)", [("k", SessionVariables.TenantId), ("v", OtherTenant.ToString())]);

        string? column = await Sql.ScalarAsync<string?>(connection, tx,
            """
            select quote_ident(a.attname) from pg_attribute a
             where a.attrelid = @t::regclass and a.attnum > 0 and not a.attisdropped and a.attgenerated = '' and a.attidentity = ''
             order by a.attnum limit 1
            """, [("t", t.Qualified)]);
        string sql = write == "update" ? $"update {t.Qualified} set {column} = {column}" : $"delete from {t.Qualified}";

        try
        {
            await using NpgsqlCommand command = new(sql, connection, tx);
            return await command.ExecuteNonQueryAsync(Ct);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.InsufficientPrivilege)
        {
            return null;
        }
        finally
        {
            await tx.RollbackAsync(Ct);
        }
    }
}
