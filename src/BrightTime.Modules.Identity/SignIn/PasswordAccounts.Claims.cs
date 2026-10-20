using System.Globalization;
using System.Security.Cryptography;
using BrightTime.Infrastructure.Security;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>A claim link to send to a young person. The token is in the email only; the database holds its hash.</summary>
public sealed record AccountClaimRequest(UserId UserId, string Email, string? Locale, string Token)
{
    /// <summary>Never the token or the address: a logged request must not carry either.</summary>
    public override string ToString() => $"AccountClaimRequest {{ UserId = {UserId} }}";
}

/// <summary>
/// What asking for a claim link came to: a link to send, an address that already has an account
/// (whose owner is told, in <see cref="InUseLocale"/>), or nothing to send.
/// </summary>
public sealed record AccountClaimIssue(AccountClaimRequest? Claim, bool AddressInUse, string? InUseLocale)
{
    internal static readonly AccountClaimIssue Nothing = new(null, false, null);
}

/// <summary>
/// The child-to-youth claim (D35): a child profile, once its owner is 13, gets a sign-in of their
/// own. The guardian names the young person's address; the young person proves it by choosing a
/// password through the emailed link, and only then does the account change hands.
/// </summary>
/// <remarks>
/// Until the claim completes the account stays a child profile, with the under-13 floor and no
/// address or password. Completing it makes it a <c>youth</c> account with both, in one
/// statement; the guardian's link and consents stay, so the guardian still manages what the
/// young person may do (<see cref="Consent.YouthCapabilityCalculator"/>). The token lives where
/// reset tokens do, under its own name, with the address it is for.
/// </remarks>
public sealed partial class PasswordAccounts
{
    /// <summary>A young person may not see the email at once; three days, as a consent email.</summary>
    public static readonly TimeSpan ClaimLifetime = TimeSpan.FromDays(3);

    private const string ClaimTokenName = "account_claim";

    /// <summary>
    /// A claim link for a child profile that may be claimed, to <paramref name="email"/>. Nothing
    /// when the profile is not an active child account or a link went out moments ago; the address's
    /// owner is to be told instead when it already has an account. The caller has checked the
    /// guardian and the age (<see cref="Consent.Guardianship.RequestClaimAsync"/>).
    /// </summary>
    public async Task<AccountClaimIssue> CreateClaimAsync(TenantId tenant, UserId child, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        string address = email.Trim();

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        Account? profile = await FindByIdAsync(connection, tx, child.Value, cancellationToken).ConfigureAwait(false);
        if (profile is not { Kind: "child", Status: "active" })
        {
            return AccountClaimIssue.Nothing;
        }

        if (await FindByEmailAsync(connection, tx, address, cancellationToken).ConfigureAwait(false) is { } owner)
        {
            return new AccountClaimIssue(null, true, owner.Locale);
        }

        DateTimeOffset now = clock.UtcNow;
        StoredClaim? existing = await ReadClaimAsync(connection, tx, child.Value, cancellationToken).ConfigureAwait(false);
        if (existing is not null && now - existing.CreatedAt < ResetResendInterval)
        {
            return AccountClaimIssue.Nothing;
        }

        // A new link replaces the last, which stops working, whatever address it was for.
        byte[] secret = RandomNumberGenerator.GetBytes(RandomBytes);
        string stored = string.Join('|',
            Convert.ToHexString(SHA256.HashData(secret)),
            (now + ClaimLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            address);
        await ExecAsync(connection, tx,
            """
            insert into identity.app_user_token (user_id, login_provider, name, value)
            values (@id, @provider, @name, @value)
            on conflict (user_id, login_provider, name) do update set value = excluded.value
            """,
            cancellationToken,
            ("id", child.Value), ("provider", TokenProvider), ("name", ClaimTokenName), ("value", stored)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        byte[] token = [.. child.Value.ToByteArray(bigEndian: true), .. secret];
        return new AccountClaimIssue(new AccountClaimRequest(child, address, profile.Locale, Base64Url(token)), false, null);
    }

    /// <summary>
    /// Completes a claim: the profile becomes a youth account with the address the link went to,
    /// now confirmed, and this password. The link works once.
    /// </summary>
    public async Task<Result<UserId>> ClaimAsync(TenantId tenant, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        Error invalid = Error.Validation("account_claim.invalid", "This link has expired or has already been used. Ask your parent or guardian for a new one.");

        if (!TryDecode(token, out Guid userId, out byte[] secret))
        {
            return invalid;
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        StoredClaim? stored = await ReadClaimAsync(connection, tx, userId, cancellationToken, forUpdate: true).ConfigureAwait(false);
        if (stored is null
            || stored.ExpiresAt <= clock.UtcNow
            || !CryptographicOperations.FixedTimeEquals(stored.Hash, SHA256.HashData(secret)))
        {
            return invalid;
        }

        if (await FindByIdAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false) is not { Kind: "child", Status: "active" })
        {
            return invalid;
        }

        if (PasswordPolicy.Check(newPassword, stored.Email) is { Count: > 0 } problems)
        {
            return Error.Validation("password.weak", problems[0].Key, new Dictionary<string, Phrase[]> { ["password"] = [.. problems] });
        }

        // Taken since the link went out: the young person needs a link to another address.
        if (await FindByEmailAsync(connection, tx, stored.Email, cancellationToken).ConfigureAwait(false) is not null)
        {
            return AddressInUse();
        }

        try
        {
            // One statement: never a child account with a password (ck_app_user_child_no_password).
            await ExecAsync(connection, tx,
                """
                update identity.app_user
                   set account_kind = 'youth', user_name = @email, normalized_user_name = upper(@email),
                       email = @email, normalized_email = upper(@email), email_confirmed = true,
                       password_hash = @hash, security_stamp = @stamp, concurrency_stamp = @concurrency,
                       access_failed_count = 0, lockout_end = null
                 where id = @id and account_kind = 'child';
                delete from identity.app_user_token where user_id = @id and login_provider = @provider and name = @name;
                """,
                cancellationToken,
                ("email", stored.Email),
                ("hash", Argon2PasswordHasher.HashPassword(newPassword)),
                ("stamp", Guid.NewGuid().ToString("N")),
                ("concurrency", Guid.NewGuid().ToString("N")),
                ("id", userId), ("provider", TokenProvider), ("name", ClaimTokenName)).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two claims to one address at once: the second finds it taken.
            return AddressInUse();
        }

        return new UserId(userId);

        static Error AddressInUse() =>
            Error.Conflict("account_claim.address_in_use", "That email address already has an account. Ask your parent or guardian to use another one.");
    }

    private sealed record StoredClaim(byte[] Hash, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, string Email);

    private static async Task<StoredClaim?> ReadClaimAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, CancellationToken ct, bool forUpdate = false)
    {
        string? value = await ScalarAsync<string?>(connection, tx,
            "select value from identity.app_user_token where user_id = @id and login_provider = @provider and name = @name"
            + (forUpdate ? " for update" : ""),
            ct, ("id", userId), ("provider", TokenProvider), ("name", ClaimTokenName)).ConfigureAwait(false);

        // The address is last, and split off whole: an address may itself contain '|'.
        string[] parts = value?.Split('|', 4) ?? [];
        if (parts.Length != 4
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long created))
        {
            return null;
        }

        return new StoredClaim(Convert.FromHexString(parts[0]), DateTimeOffset.FromUnixTimeSeconds(expires), DateTimeOffset.FromUnixTimeSeconds(created), parts[3]);
    }
}
