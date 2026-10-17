using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>An external account connected to a BrightTime account.</summary>
public sealed record ExternalLoginSummary(string Issuer, string DisplayName, DateTimeOffset LinkedAt);

/// <summary>
/// Connections between BrightTime accounts and accounts at external OpenID Connect providers,
/// as <c>brighttime_auth</c> (decision D17).
/// </summary>
/// <remarks>
/// <para>
/// An external account is identified by its issuer and subject, the pair the provider promises
/// never to reuse. Email is never used: not to find an account, and not to connect one. A
/// provider's email claim can be unverified, or recycled when a domain changes hands, and
/// matching on it would let whoever controls that address at the provider take the account.
/// </para>
/// <para>
/// A connection is made only by <see cref="LinkAsync"/>, which the web layer calls when a
/// person who is already signed in completes the provider's flow. Nothing here creates an
/// account.
/// </para>
/// </remarks>
public sealed class ExternalLogins(NpgsqlDataSource authDataSource, IClock clock)
{
    /// <summary>The account connected to this external identity, if it can sign in.</summary>
    public async Task<UserId?> FindAsync(TenantId tenant, string issuer, string subject, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            select u.id
              from identity.app_user_login l
              join identity.app_user u on u.id = l.user_id
             where l.login_provider = @issuer and l.provider_key = @subject
               and u.status = 'active' and u.account_kind <> 'child' and u.deleted_at is null
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("issuer", issuer));
        command.Parameters.Add(new NpgsqlParameter("subject", subject));

        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (id is not Guid user)
        {
            return null;
        }

        await using NpgsqlCommand touch = new("update identity.app_user set last_login_at = @now where id = @id", connection, tx);
        touch.Parameters.Add(new NpgsqlParameter("now", clock.UtcNow));
        touch.Parameters.Add(new NpgsqlParameter("id", user));
        await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserId(user);
    }

    /// <summary>Connects an external identity to an account. Refused if it is already connected to any account.</summary>
    public async Task<Result> LinkAsync(
        TenantId tenant,
        UserId user,
        string issuer,
        string subject,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        // One connection per provider per account: a second would only confuse which one signs in.
        await using (NpgsqlCommand existing = new(
            "select count(*) from identity.app_user_login where user_id = @u and login_provider = @issuer", connection, tx))
        {
            existing.Parameters.Add(new NpgsqlParameter("u", user.Value));
            existing.Parameters.Add(new NpgsqlParameter("issuer", issuer));
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is > 0L)
            {
                return Error.Conflict("external.already_connected", $"Your account is already connected to {displayName}. Disconnect it first to connect a different one.");
            }
        }

        await using NpgsqlCommand insert = new(
            """
            insert into identity.app_user_login (login_provider, provider_key, provider_display_name, user_id, linked_at)
            values (@issuer, @subject, @name, @u, @now)
            """, connection, tx);
        insert.Parameters.Add(new NpgsqlParameter("issuer", issuer));
        insert.Parameters.Add(new NpgsqlParameter("subject", subject));
        insert.Parameters.Add(new NpgsqlParameter("name", displayName));
        insert.Parameters.Add(new NpgsqlParameter("u", user.Value));
        insert.Parameters.Add(new NpgsqlParameter("now", clock.UtcNow));
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Connected to some other account, possibly in another tenant; say no more than that.
            return Error.Conflict("external.in_use", $"That {displayName} account is already connected to a different BrightTime account.");
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<bool> UnlinkAsync(TenantId tenant, UserId user, string issuer, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            "delete from identity.app_user_login where user_id = @u and login_provider = @issuer", connection, tx);
        command.Parameters.Add(new NpgsqlParameter("u", user.Value));
        command.Parameters.Add(new NpgsqlParameter("issuer", issuer));
        int removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed > 0;
    }

    public async Task<IReadOnlyList<ExternalLoginSummary>> ListAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            select login_provider, coalesce(provider_display_name, login_provider), linked_at
              from identity.app_user_login where user_id = @u order by linked_at
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("u", user.Value));

        List<ExternalLoginSummary> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ExternalLoginSummary(reader.GetString(0), reader.GetString(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false)));
        }

        return rows;
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenant, CancellationToken ct)
    {
        NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand set = new("select set_config(@key, @value, true)", connection, tx);
        set.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
        set.Parameters.Add(new NpgsqlParameter("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
        await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return tx;
    }
}
