using System.Globalization;
using System.Security.Cryptography;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Domain;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>
/// Accounts made by address, before their owner has set a password: adults who run
/// organisations, applying or invited (decision D23), and adults and young people registering
/// from the app (D34, D35).
/// </summary>
/// <remarks>
/// <para>
/// An account made here has no password. Its owner sets one through the ordinary emailed link,
/// which also confirms the address. Until then it cannot sign in, so making one in someone
/// else's name gets an attacker nothing but an email sent to the real owner.
/// </para>
/// <para>
/// Callers must not tell anyone whether an address already had an account: the answer to an
/// application or an invitation reads the same either way. <see cref="FindOrCreateAdultAsync"/>
/// says which it was so the caller can choose a membership state, not so it can be shown.
/// Never a child profile: those are made by a guardian, with consent (D31).
/// </para>
/// </remarks>
public sealed class AccountProvisioning(NpgsqlDataSource authDataSource)
{
    public const int MaxEmailLength = 320;

    /// <summary>The adult account with this address, made if there was none. No role is granted here.</summary>
    /// <returns>The account, and whether it was made just now. Null when the address belongs to a youth or child account.</returns>
    /// <remarks>
    /// Roles wait for <see cref="GrantRoleAsync"/>, called when a membership becomes active: an
    /// address typed into an application or an invitation must not change what an existing
    /// account can do before its owner has said yes.
    /// </remarks>
    public Task<(UserId User, bool Created)?> FindOrCreateAdultAsync(TenantId tenant, string email, CancellationToken cancellationToken = default) =>
        FindOrCreateAsync(tenant, email, AccountKind.Adult, null, cancellationToken);

    /// <summary>
    /// The account with this address, made as a youth account (13 to 17, D35) if there was none. An
    /// existing account is left as it is, whatever kind: an address typed into a form must not
    /// change someone else's account. Only the birth month is kept, as for a child profile.
    /// </summary>
    /// <returns>The account, and whether it was made just now. Null when the address belongs to a child profile.</returns>
    public Task<(UserId User, bool Created)?> FindOrCreateYouthAsync(TenantId tenant, string email, DateOnly birthMonth, AgeBand band, CancellationToken cancellationToken = default)
    {
        if (birthMonth.Day != 1 || !band.IsYouth())
        {
            throw new ArgumentOutOfRangeException(nameof(band), "A youth account is for 13 to 17, by the first of the birth month.");
        }

        return FindOrCreateAsync(tenant, email, AccountKind.Youth, (birthMonth, band), cancellationToken);
    }

    private async Task<(UserId User, bool Created)?> FindOrCreateAsync(TenantId tenant, string email, string kind, (DateOnly Month, AgeBand Band)? age, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        string address = email.Trim();
        if (address.Length > MaxEmailLength)
        {
            throw new ArgumentOutOfRangeException(nameof(email));
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, tx, "select set_config(@key, @value, true)", cancellationToken,
            ("key", SessionVariables.TenantId), ("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture))).ConfigureAwait(false);

        Guid id = UuidV7.NewId();
        string stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await ExecAsync(connection, tx,
            """
            insert into identity.app_user
              (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
               security_stamp, concurrency_stamp, account_kind, status, birth_month, age_band)
            values (@id, @tenant, @email, upper(@email), @email, upper(@email), false, @stamp, @concurrency, @kind, 'active', @month, @band)
            on conflict (tenant_id, normalized_email) do nothing
            """, cancellationToken,
            ("id", id), ("tenant", tenant.Value), ("email", address), ("stamp", stamp), ("concurrency", Guid.NewGuid().ToString("N")),
            ("kind", kind),
            ("month", age is { } a ? a.Month : DBNull.Value),
            ("band", age is { } b ? b.Band.ToDbValue() : DBNull.Value)).ConfigureAwait(false);

        (Guid existing, string found) = await ReadAsync(connection, tx, address, cancellationToken).ConfigureAwait(false);
        if (found is not (AccountKind.Adult or AccountKind.Youth) || (kind == AccountKind.Adult && found != AccountKind.Adult))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (new UserId(existing), existing == id);
    }

    /// <summary>
    /// Adds a global role to an adult account, for a membership that has just become active.
    /// Kept when the membership ends: organisation-scoped permissions also need a live
    /// membership, so the role alone reaches nothing. The person's next sign-in, or a refresh
    /// of their session, picks it up.
    /// </summary>
    public async Task GrantRoleAsync(TenantId tenant, UserId user, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, tx, "select set_config(@key, @value, true)", cancellationToken,
            ("key", SessionVariables.TenantId), ("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await ExecAsync(connection, tx,
            """
            insert into identity.app_user_role (user_id, role_id)
            select u.id, r.id
              from identity.app_user u
              join identity.app_role r on r.tenant_id = u.tenant_id and r.name = @role
             where u.id = @user and u.account_kind = 'adult' and u.deleted_at is null
            on conflict do nothing
            """, cancellationToken, ("user", user.Value), ("role", role)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The addresses of adult accounts, for an organisation's staff page. Others are left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> EmailsAsync(TenantId tenant, IReadOnlyCollection<Guid> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        Dictionary<Guid, string> emails = [];
        if (users.Count == 0)
        {
            return emails;
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, tx, "select set_config(@key, @value, true)", cancellationToken,
            ("key", SessionVariables.TenantId), ("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await using (NpgsqlCommand command = new(
            "select id, email from identity.app_user where id = any(@ids) and account_kind = 'adult' and deleted_at is null and email is not null",
            connection, tx))
        {
            command.Parameters.Add(new NpgsqlParameter("ids", users.ToArray()));
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                emails[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return emails;
    }

    /// <summary>The global role that goes with an organisation role: owners and admins administer, the rest work on listings.</summary>
    public static string GlobalRoleFor(string organisationRole) =>
        organisationRole is "owner" or "admin" ? Authorization.BrightTimeRoles.ProviderAdmin : Authorization.BrightTimeRoles.ProgramStaff;

    private static async Task<(Guid Id, string Kind)> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string email, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(
            "select id, account_kind from identity.app_user where normalized_email = upper(@email) and deleted_at is null", connection, tx);
        command.Parameters.Add(new NpgsqlParameter("email", email));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // None: the address belongs to an erased account, which keeps its row (and its address
        // pseudonymised) for the audit trail. Treated as unusable, never reused.
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? (reader.GetGuid(0), reader.GetString(1)) : (Guid.Empty, "none");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every statement is a constant in this class; values are bound parameters.")]
    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, tx);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
