using System.Data;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// Helpers for running SQL in the integration tests.
/// </summary>
/// <remarks>
/// Test data is seeded as the superuser, which bypasses row-level security by design.
/// Assertions run as the application role, with the tenant and actor set inside the
/// transaction exactly as <c>TenantSessionInterceptor</c> does. That is the only way
/// to test isolation honestly: a superuser query would see every row and prove nothing.
/// </remarks>
internal static class Sql
{
    public static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>The only role names a test may assume. Anything else is refused before it reaches SQL.</summary>
    private static readonly string[] AllowedRoles = ["brighttime_app", "brighttime_auth", "brighttime_finder", "brighttime_jobs"];

    /// <summary>Runs <paramref name="body"/> as <paramref name="role"/> with the tenant and actor set, then rolls back.</summary>
    public static async Task<T> AsRoleAsync<T>(
        string connectionString,
        string role,
        Guid tenant,
        Guid? actor,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body,
        CancellationToken ct)
    {
        if (Array.IndexOf(AllowedRoles, role) < 0)
        {
            throw new ArgumentException($"'{role}' is not a role the tests may assume.", nameof(role));
        }

        await using NpgsqlConnection connection = await OpenAsync(connectionString, ct).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ExecAsync(connection, transaction, "set local role " + role, []).ConfigureAwait(false);
        await ExecAsync(
            connection,
            transaction,
            "select set_config('brighttime.tenant_id', @tenant, true), set_config('brighttime.actor_id', @actor, true)",
            [("tenant", tenant.ToString("D")), ("actor", actor?.ToString("D") ?? string.Empty)]).ConfigureAwait(false);

        try
        {
            return await body(connection, transaction).ConfigureAwait(false);
        }
        finally
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
    }

    public static Task<T> AsAppAsync<T>(
        string connectionString,
        Guid tenant,
        Guid? actor,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body,
        CancellationToken ct) =>
        AsRoleAsync(connectionString, "brighttime_app", tenant, actor, body, ct);

    /// <summary>The non-returning form, for a body that only writes.</summary>
    public static Task AsAppAsync(
        string connectionString,
        Guid tenant,
        Guid? actor,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> body,
        CancellationToken ct) =>
        AsAppAsync<bool>(connectionString, tenant, actor, async (c, t) =>
        {
            await body(c, t).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Runs a statement with named parameters. The SQL is always a literal in the tests.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test harness. Every statement is a literal in the test source; caller values are bound as parameters.")]
    public static async Task ExecAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test harness. Every statement is a literal in the test source; caller values are bound as parameters.")]
    public static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        }

        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A tenant row, the parent of everything else in a test.</summary>
    public static async Task SeedTenantAsync(NpgsqlConnection connection, Guid tenant, string slug)
    {
        await ExecAsync(connection, null,
            """
            insert into platform.tenant (id, slug, display_name, region_name, region_kind, default_locale, timezone)
            values (@id, @slug, 'Test region', 'Kettle County, New York', 'county', 'en', 'America/New_York')
            on conflict (id) do nothing
            """,
            [("id", tenant), ("slug", slug)]).ConfigureAwait(false);
    }

    /// <summary>A minimal account. Pass <paramref name="kind"/> as adult, youth or child.</summary>
    public static async Task SeedUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid id,
        Guid tenant,
        string kind,
        string status = "active")
    {
        await ExecAsync(connection, transaction,
            """
            insert into identity.app_user
              (id, tenant_id, user_name, normalized_user_name, security_stamp, concurrency_stamp,
               account_kind, status)
            values (@id, @tenant, @name, @name, 'stamp', 'concurrency', @kind, @status)
            """,
            [("id", id), ("tenant", tenant), ("name", "user-" + id.ToString("N")), ("kind", kind), ("status", status)])
            .ConfigureAwait(false);
    }
}
