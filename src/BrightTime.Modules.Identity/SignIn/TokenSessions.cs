using System.Globalization;
using System.Security.Cryptography;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>How long a token session lasts: unused for <see cref="Idle"/>, or in all for <see cref="Absolute"/>.</summary>
public sealed record SessionLifetime(TimeSpan Idle, TimeSpan Absolute);

/// <summary>A refresh token as handed to a client, once: only its hash is stored.</summary>
public sealed record IssuedRefreshToken(string Token, Guid FamilyId, DateTimeOffset ExpiresAt);

/// <summary>What presenting a refresh token did.</summary>
public abstract record RefreshOutcome
{
    private RefreshOutcome()
    {
    }

    /// <summary>A new token in the same family; the presented one is spent.</summary>
    public sealed record Rotated(UserId User, IssuedRefreshToken Next, string AuthMethod, DateTimeOffset AuthenticatedAt) : RefreshOutcome;

    /// <summary>Unknown, expired, revoked by sign-out, or the account can no longer sign in.</summary>
    public sealed record Invalid : RefreshOutcome;

    /// <summary>
    /// A token already spent was presented again: it was copied, or a client is replaying it.
    /// The whole family is revoked, so whichever party holds the newer token loses it too (T14).
    /// </summary>
    public sealed record Reused(UserId User) : RefreshOutcome;
}

/// <summary>
/// Refresh tokens for the API's clients: rotating, family-chained, stored only as hashes
/// (<c>identity.refresh_token</c>), as <c>brighttime_auth</c>.
/// </summary>
/// <remarks>
/// <para>
/// A sign-in starts a family. Each refresh spends its token and issues the next in the family,
/// lasting <see cref="SessionLifetime.Idle"/> from then, but never past the family's start plus
/// <see cref="SessionLifetime.Absolute"/>. Presenting a spent token revokes the family: the one
/// real holder signs in again, and a thief's copy is dead.
/// </para>
/// <para>
/// Two refreshes racing with the same token are the same case: the second finds it spent and
/// revokes the family. A client must not refresh concurrently.
/// </para>
/// <para>
/// Signing out ends one family; signing out everywhere ends every family and changes the
/// security stamp, which ends cookie sessions and access tokens at their next check; a
/// password reset revokes every family too (<see cref="PasswordAccounts.ResetAsync"/>).
/// </para>
/// </remarks>
public sealed class TokenSessions(NpgsqlDataSource authDataSource, IClock clock)
{
    private const int TokenBytes = 32;

    /// <summary>Starts a family for a sign-in proved by <paramref name="authMethod"/>.</summary>
    public async Task<IssuedRefreshToken> IssueAsync(TenantId tenant, UserId user, string authMethod, string? deviceId, SessionLifetime lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentException.ThrowIfNullOrWhiteSpace(authMethod);

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;
        IssuedRefreshToken issued = await InsertAsync(connection, tx, user.Value, UuidV7.NewId(), authMethod, Trim(deviceId), now, Min(now + lifetime.Idle, now + lifetime.Absolute), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return issued;
    }

    /// <summary>Spends <paramref name="token"/> and issues the next in its family, or says why not.</summary>
    /// <remarks><paramref name="lifetimeFor"/> chooses the lifetime by account kind (adult, youth).</remarks>
    public async Task<RefreshOutcome> RotateAsync(TenantId tenant, string token, Func<string, SessionLifetime> lifetimeFor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lifetimeFor);
        if (!TryHash(token, out byte[] hash))
        {
            return new RefreshOutcome.Invalid();
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        Stored? stored = await ReadAsync(connection, tx, hash, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return new RefreshOutcome.Invalid();
        }

        DateTimeOffset now = clock.UtcNow;
        if (stored.UsedAt is not null)
        {
            // Spent already: a copy was kept. Revoke whatever is left of the family.
            await RevokeAsync(connection, tx, "family_id = @key", stored.FamilyId, "reuse", cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RefreshOutcome.Reused(new UserId(stored.UserId));
        }

        // Revoked unspent (signed out, reset, or its family ended by a reuse): no use, and no alarm.
        if (stored.RevokedAt is not null || stored.ExpiresAt <= now || !stored.AccountCanSignIn)
        {
            return new RefreshOutcome.Invalid();
        }

        SessionLifetime lifetime = lifetimeFor(stored.AccountKind);
        await using (NpgsqlCommand spend = new("update identity.refresh_token set used_at = @now where id = @id and used_at is null", connection, tx))
        {
            spend.Parameters.Add(new NpgsqlParameter("now", now));
            spend.Parameters.Add(new NpgsqlParameter("id", stored.Id));
            if (await spend.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return new RefreshOutcome.Invalid();
            }
        }

        DateTimeOffset expires = Min(now + lifetime.Idle, stored.FamilyStartedAt + lifetime.Absolute);
        if (expires <= now)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RefreshOutcome.Invalid();
        }

        IssuedRefreshToken next = await InsertAsync(connection, tx, stored.UserId, stored.FamilyId, stored.AuthMethod, stored.DeviceId, now, expires, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RefreshOutcome.Rotated(new UserId(stored.UserId), next, stored.AuthMethod, stored.FamilyStartedAt);
    }

    /// <summary>Signs one session out: revokes the family <paramref name="token"/> belongs to. The account, when there was one.</summary>
    public async Task<UserId?> SignOutAsync(TenantId tenant, string token, CancellationToken cancellationToken = default)
    {
        if (!TryHash(token, out byte[] hash))
        {
            return null;
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        Stored? stored = await ReadAsync(connection, tx, hash, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        await RevokeAsync(connection, tx, "family_id = @key", stored.FamilyId, "sign_out", cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserId(stored.UserId);
    }

    /// <summary>
    /// Signs out everywhere: every token family is revoked and the security stamp changes, so
    /// cookie sessions and access tokens stop at their next check.
    /// </summary>
    public async Task SignOutEverywhereAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await RevokeAsync(connection, tx, "user_id = @key", user.Value, "sign_out_everywhere", cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand stamp = new("update identity.app_user set security_stamp = @stamp where id = @id", connection, tx))
        {
            stamp.Parameters.Add(new NpgsqlParameter("stamp", Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
            stamp.Parameters.Add(new NpgsqlParameter("id", user.Value));
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Stored(
        Guid Id, Guid UserId, Guid FamilyId, string AuthMethod, string? DeviceId, DateTimeOffset ExpiresAt,
        DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt, string? RevokedReason, DateTimeOffset FamilyStartedAt,
        string AccountKind, bool AccountCanSignIn);

    private static async Task<Stored?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, byte[] hash, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(
            """
            select t.id, t.user_id, t.family_id, t.auth_method, t.device_id, t.expires_at, t.used_at, t.revoked_at, t.revoked_reason,
                   (select min(f.issued_at) from identity.refresh_token f where f.family_id = t.family_id),
                   u.account_kind, (u.status = 'active' and u.deleted_at is null and u.account_kind <> 'child')
              from identity.refresh_token t
              join identity.app_user u on u.id = t.user_id
             where t.token_hash = @hash
               for update of t
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("hash", hash));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Stored(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
            await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4),
            await reader.GetFieldValueAsync<DateTimeOffset>(5, ct).ConfigureAwait(false),
            await reader.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(6, ct).ConfigureAwait(false),
            await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(7, ct).ConfigureAwait(false),
            await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? null : reader.GetString(8),
            await reader.GetFieldValueAsync<DateTimeOffset>(9, ct).ConfigureAwait(false),
            reader.GetString(10),
            reader.GetBoolean(11));
    }

    private static async Task<IssuedRefreshToken> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, Guid family, string authMethod, string? deviceId,
        DateTimeOffset now, DateTimeOffset expires, CancellationToken ct)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(TokenBytes);
        await using NpgsqlCommand command = new(
            """
            insert into identity.refresh_token (id, user_id, family_id, token_hash, device_id, auth_method, issued_at, expires_at)
            values (@id, @user, @family, @hash, @device, @method, @now, @expires)
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("id", UuidV7.NewId()));
        command.Parameters.Add(new NpgsqlParameter("user", user));
        command.Parameters.Add(new NpgsqlParameter("family", family));
        command.Parameters.Add(new NpgsqlParameter("hash", SHA256.HashData(secret)));
        command.Parameters.Add(new NpgsqlParameter("device", (object?)deviceId ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("method", authMethod));
        command.Parameters.Add(new NpgsqlParameter("now", now));
        command.Parameters.Add(new NpgsqlParameter("expires", expires));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return new IssuedRefreshToken(Base64Url(secret), family, expires);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The predicate is one of two constants in this class; values are bound parameters.")]
    private static async Task RevokeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string predicate, Guid key, string reason, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(
            $"update identity.refresh_token set revoked_at = now(), revoked_reason = @reason where {predicate} and revoked_at is null",
            connection, tx);
        command.Parameters.Add(new NpgsqlParameter("reason", reason));
        command.Parameters.Add(new NpgsqlParameter("key", key));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenant, CancellationToken ct)
    {
        NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand command = new("select set_config(@key, @value, true)", connection, tx);
        command.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
        command.Parameters.Add(new NpgsqlParameter("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return tx;
    }

    /// <summary>The stored hash of a token, or false for anything that is not one of ours.</summary>
    private static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
        {
            return false;
        }

        try
        {
            string padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            byte[] secret = Convert.FromBase64String(padded);
            if (secret.Length != TokenBytes)
            {
                return false;
            }

            hash = SHA256.HashData(secret);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Trim(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim()[..Math.Min(deviceId.Trim().Length, 100)];

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
