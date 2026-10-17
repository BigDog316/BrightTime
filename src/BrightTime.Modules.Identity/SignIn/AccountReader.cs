using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>An account as sign-in needs it: who, in which state, with which permissions.</summary>
public sealed record AccountSnapshot(
    UserId UserId,
    TenantId TenantId,
    string UserName,
    string AccountKind,
    string Status,
    string? Locale,
    string SecurityStamp,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions)
{
    public bool IsActive => Status == "active";
}

/// <summary>
/// Reads accounts for sign-in, as <c>brighttime_auth</c>.
/// </summary>
/// <remarks>
/// Sign-in reads <c>identity.app_user</c> before any actor exists, which the RESTRICTIVE
/// actor policy forbids for the application role. That is why this path has its own
/// database principal with grants on the Identity tables and nothing else (see
/// <c>db/schema/99-rls-and-grants.sql</c>). The tenant is still set, so the tenant policy
/// still applies: one tenant's sign-in cannot see another's accounts.
/// </remarks>
public sealed class AccountReader(NpgsqlDataSource authDataSource)
{
    private const string AccountColumns =
        "u.id, u.tenant_id, u.user_name::text, u.account_kind, u.status, u.locale, u.security_stamp";

    public async Task<AccountSnapshot?> GetAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AccountSnapshot> found = await QueryAsync(
            tenant,
            $"select {AccountColumns} from identity.app_user u where u.id = @user and u.deleted_at is null",
            [new NpgsqlParameter("user", user.Value)],
            cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>The account with this email, whatever its state, for filtering the audit log by person.</summary>
    public async Task<UserId?> FindByEmailAsync(TenantId tenant, string email, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, string> found = await LookupAsync(
            tenant,
            "select id, email::text from identity.app_user where normalized_email = upper(btrim(@email))",
            new NpgsqlParameter("email", email),
            cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : new UserId(found.Keys.First());
    }

    /// <summary>
    /// Email addresses for a page of audit rows, in one query. Deleted accounts are included:
    /// the trail outlives the account, and erasure pseudonymises the address itself.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, string>> EmailsAsync(TenantId tenant, IReadOnlyCollection<Guid> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        return users.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>())
            : LookupAsync(
                tenant,
                "select id, coalesce(email::text, user_name::text) from identity.app_user where id = any(@users)",
                new NpgsqlParameter("users", users.ToArray()),
                cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both callers pass a constant query; the value is a bound parameter.")]
    private async Task<IReadOnlyDictionary<Guid, string>> LookupAsync(TenantId tenant, string sql, NpgsqlParameter parameter, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (NpgsqlCommand set = new("select set_config(@key, @value, true)", connection, tx))
        {
            set.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
            set.Parameters.Add(new NpgsqlParameter("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Dictionary<Guid, string> found = [];
        await using (NpgsqlCommand command = new(sql, connection, tx))
        {
            command.Parameters.Add(parameter);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    /// Active adult accounts holding at least one of the named roles, for the development
    /// sign-in list. Never used on a production path.
    /// </summary>
    public Task<IReadOnlyList<AccountSnapshot>> ListWithRolesAsync(
        TenantId tenant,
        IReadOnlyCollection<string> roles,
        int limit,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            tenant,
            $"""
             select {AccountColumns} from identity.app_user u
             where u.status = 'active' and u.account_kind = 'adult' and u.deleted_at is null
               and exists (select 1 from identity.app_user_role ur
                           join identity.app_role r on r.id = ur.role_id
                           where ur.user_id = u.id and r.name = any(@roles))
             order by u.user_name
             limit @limit
             """,
            [new NpgsqlParameter("roles", roles.ToArray()), new NpgsqlParameter("limit", limit)],
            cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The account query is one of two constants in this class; every value is a bound parameter.")]
    private async Task<IReadOnlyList<AccountSnapshot>> QueryAsync(
        TenantId tenant,
        string accountSql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (NpgsqlCommand set = new("select set_config(@key, @value, true)", connection, tx))
        {
            set.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
            set.Parameters.Add(new NpgsqlParameter("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        List<(Guid Id, Guid Tenant, string Name, string Kind, string Status, string? Locale, string Stamp)> rows = [];
        await using (NpgsqlCommand command = new(accountSql, connection, tx))
        {
            command.Parameters.AddRange(parameters);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetString(5), reader.GetString(6)));
            }
        }

        Dictionary<Guid, (List<string> Roles, List<string> Permissions)> grants = rows.ToDictionary(r => r.Id, _ => (new List<string>(), new List<string>()));
        if (rows.Count > 0)
        {
            await using NpgsqlCommand command = new(
                """
                select ur.user_id, r.name::text, rc.claim_value
                from identity.app_user_role ur
                join identity.app_role r on r.id = ur.role_id
                left join identity.app_role_claim rc on rc.role_id = r.id and rc.claim_type = @permission
                where ur.user_id = any(@users)
                order by 1, 2, 3
                """, connection, tx);
            command.Parameters.Add(new NpgsqlParameter("permission", BrightTimeClaims.Permission));
            command.Parameters.Add(new NpgsqlParameter("users", rows.Select(r => r.Id).ToArray()));

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                (List<string> roleList, List<string> permissionList) = grants[reader.GetGuid(0)];
                string role = reader.GetString(1);
                if (!roleList.Contains(role, StringComparer.Ordinal))
                {
                    roleList.Add(role);
                }

                // Only permissions the code defines are honoured, whatever the table says.
                if (!await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) && reader.GetString(2) is { } permission
                    && Authorization.Permissions.All.Contains(permission)
                    && !permissionList.Contains(permission, StringComparer.Ordinal))
                {
                    permissionList.Add(permission);
                }
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new AccountSnapshot(
                new UserId(r.Id), new TenantId(r.Tenant), r.Name, r.Kind, r.Status, r.Locale, r.Stamp,
                grants[r.Id].Roles, grants[r.Id].Permissions)),
        ];
    }
}
