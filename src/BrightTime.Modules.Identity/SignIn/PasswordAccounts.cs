using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Infrastructure.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>What a password check found. <see cref="Failure"/> is for the audit trail only.</summary>
public sealed record PasswordCheckOutcome(UserId? SignedIn, UserId? Account, string? Failure)
{
    public const string NoAccount = "no_account";
    public const string NotEligible = "account_not_eligible";
    public const string Locked = "account_locked";
    public const string WrongPassword = "wrong_password";
    public const string LockoutStarted = "lockout_started";

    internal static PasswordCheckOutcome Failed(UserId? account, string failure) => new(null, account, failure);
}

/// <summary>A reset link to send. The token is in the email only; the database holds its hash.</summary>
public sealed record PasswordResetRequest(UserId UserId, string Email, string? Locale, string Token);

/// <summary>
/// Password sign-in and password reset, as <c>brighttime_auth</c> (threat model T21).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here tells a caller whether an email is registered. A wrong password, an unknown
/// email, a locked or inactive account and a child account all come back as the same null,
/// after the same Argon2 work.
/// </para>
/// <para>
/// Five wrong passwords lock the account for fifteen minutes. The failure count is read and
/// written under <c>FOR UPDATE</c>, so parallel guesses cannot each see a count of four.
/// </para>
/// <para>
/// Reset tokens are 256 random bits, stored as a SHA-256 hash in <c>app_user_token</c> with an
/// expiry, single-use, one per account. A successful reset changes the security stamp, so
/// every existing session is rejected the next time its cookie is revalidated.
/// </para>
/// </remarks>
public sealed class PasswordAccounts(NpgsqlDataSource authDataSource, IClock clock)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(60);

    /// <summary>A second reset email within this window is silently not sent, so a form cannot be used to flood an inbox.</summary>
    public static readonly TimeSpan ResetResendInterval = TimeSpan.FromMinutes(1);

    private const string TokenProvider = "brighttime";
    private const string ResetTokenName = "password_reset";
    private const int RandomBytes = 32;

    /// <summary>The account the email and password belong to, or null for any failure at all.</summary>
    public async Task<UserId?> CheckPasswordAsync(TenantId tenant, string email, string password, CancellationToken cancellationToken = default) =>
        (await CheckPasswordWithOutcomeAsync(tenant, email, password, cancellationToken).ConfigureAwait(false)).SignedIn;

    /// <summary>
    /// The same check, saying what happened, for the audit trail. The person is still shown one
    /// message whatever the outcome (T21); only the audit log tells them apart.
    /// </summary>
    public Task<PasswordCheckOutcome> CheckPasswordWithOutcomeAsync(TenantId tenant, string email, string password, CancellationToken cancellationToken = default) =>
        CheckAsync(tenant, (c, tx, ct) => FindByEmailAsync(c, tx, email, ct), password, cancellationToken);

    /// <summary>
    /// Step-up: whether this signed-in account's own password is the one given. Failures count
    /// toward the same lockout as sign-in, so a borrowed session cannot be used to guess it.
    /// </summary>
    public async Task<bool> ConfirmPasswordAsync(TenantId tenant, UserId user, string password, CancellationToken cancellationToken = default) =>
        (await CheckAsync(tenant, (c, tx, ct) => FindByIdAsync(c, tx, user.Value, ct), password, cancellationToken).ConfigureAwait(false)).SignedIn == user;

    /// <summary>Whether the account has a password at all, so step-up knows what to offer.</summary>
    public async Task<bool> HasPasswordAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<bool>(connection, tx,
            "select password_hash is not null from identity.app_user where id = @id", cancellationToken, ("id", user.Value)).ConfigureAwait(false);
    }

    private async Task<PasswordCheckOutcome> CheckAsync(
        TenantId tenant,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<Account?>> find,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        Account? account = await find(connection, tx, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;

        if (account is null || !account.CanSignIn || account.LockoutEnd > now)
        {
            Argon2PasswordHasher.VerifyAgainstNothing(password);
            return account is null
                ? PasswordCheckOutcome.Failed(null, PasswordCheckOutcome.NoAccount)
                : PasswordCheckOutcome.Failed(new UserId(account.Id),
                    account.LockoutEnd > now ? PasswordCheckOutcome.Locked : PasswordCheckOutcome.NotEligible);
        }

        PasswordCheck check = Argon2PasswordHasher.Verify(account.PasswordHash, password);
        if (!check.IsValid)
        {
            int failures = account.AccessFailedCount + 1;
            bool lockNow = account.LockoutEnabled && failures >= MaxFailedAttempts;
            await ExecAsync(connection, tx,
                "update identity.app_user set access_failed_count = @f, lockout_end = @end where id = @id",
                cancellationToken,
                ("f", lockNow ? 0 : failures),
                ("end", lockNow ? now + LockoutDuration : (object)DBNull.Value),
                ("id", account.Id)).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PasswordCheckOutcome.Failed(new UserId(account.Id),
                lockNow ? PasswordCheckOutcome.LockoutStarted : PasswordCheckOutcome.WrongPassword);
        }

        await ExecAsync(connection, tx,
            """
            update identity.app_user
               set access_failed_count = 0, lockout_end = null, last_login_at = @now,
                   password_hash = coalesce(@rehash, password_hash)
             where id = @id
            """,
            cancellationToken,
            ("now", now),
            ("rehash", check.NeedsRehash ? Argon2PasswordHasher.HashPassword(password) : (object)DBNull.Value),
            ("id", account.Id)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PasswordCheckOutcome(new UserId(account.Id), new UserId(account.Id), null);
    }

    /// <summary>
    /// A reset link for an account that can sign in, or null: for an unknown email, for an
    /// account that cannot have a password, or when one was sent moments ago. The caller
    /// responds identically either way.
    /// </summary>
    public async Task<PasswordResetRequest?> CreateResetAsync(TenantId tenant, string email, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        Account? account = await FindByEmailAsync(connection, tx, email, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.CanSignIn)
        {
            return null;
        }

        DateTimeOffset now = clock.UtcNow;
        StoredToken? existing = await ReadTokenAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null && now - existing.CreatedAt < ResetResendInterval)
        {
            return null;
        }

        byte[] secret = RandomNumberGenerator.GetBytes(RandomBytes);
        string stored = string.Join('|',
            Convert.ToHexString(SHA256.HashData(secret)),
            (now + ResetLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        await ExecAsync(connection, tx,
            """
            insert into identity.app_user_token (user_id, login_provider, name, value)
            values (@id, @provider, @name, @value)
            on conflict (user_id, login_provider, name) do update set value = excluded.value
            """,
            cancellationToken,
            ("id", account.Id), ("provider", TokenProvider), ("name", ResetTokenName), ("value", stored)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        byte[] token = [.. account.Id.ToByteArray(bigEndian: true), .. secret];
        return new PasswordResetRequest(new UserId(account.Id), account.Email!, account.Locale, Base64Url(token));
    }

    /// <summary>Sets a new password from a reset link. The link works once and ends every session.</summary>
    public async Task<Result<UserId>> ResetAsync(TenantId tenant, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        Error invalid = Error.Validation("password_reset.invalid", "This link has expired or has already been used. Ask for a new one.");

        if (!TryDecode(token, out Guid userId, out byte[] secret))
        {
            return invalid;
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        StoredToken? stored = await ReadTokenAsync(connection, tx, userId, cancellationToken, forUpdate: true).ConfigureAwait(false);
        if (stored is null
            || stored.ExpiresAt <= clock.UtcNow
            || !CryptographicOperations.FixedTimeEquals(stored.Hash, SHA256.HashData(secret)))
        {
            return invalid;
        }

        string? email = await ScalarAsync<string?>(connection, tx,
            "select email::text from identity.app_user where id = @id and status = 'active' and account_kind <> 'child' and deleted_at is null",
            cancellationToken, ("id", userId)).ConfigureAwait(false);
        if (email is null)
        {
            return invalid;
        }

        if (PasswordPolicy.Check(newPassword, email) is { Count: > 0 } problems)
        {
            return Error.Validation("password.weak", problems[0].Key, new Dictionary<string, Phrase[]> { ["password"] = [.. problems] });
        }

        // Proving control of the inbox also confirms the address.
        await ExecAsync(connection, tx,
            """
            update identity.app_user
               set password_hash = @hash, security_stamp = @stamp, concurrency_stamp = @concurrency,
                   access_failed_count = 0, lockout_end = null, email_confirmed = true
             where id = @id;
            delete from identity.app_user_token where user_id = @id and login_provider = @provider and name = @name;
            """,
            cancellationToken,
            ("hash", Argon2PasswordHasher.HashPassword(newPassword)),
            ("stamp", Guid.NewGuid().ToString("N")),
            ("concurrency", Guid.NewGuid().ToString("N")),
            ("id", userId), ("provider", TokenProvider), ("name", ResetTokenName)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserId(userId);
    }

    private sealed record Account(
        Guid Id,
        string? Email,
        string? PasswordHash,
        string Status,
        string Kind,
        string? Locale,
        int AccessFailedCount,
        DateTimeOffset? LockoutEnd,
        bool LockoutEnabled)
    {
        /// <summary>A child account never has a password (ck_app_user_child_no_password); a guardian acts for it.</summary>
        public bool CanSignIn => Status == "active" && Kind != "child";
    }

    private sealed record StoredToken(byte[] Hash, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenant, CancellationToken ct)
    {
        NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await ExecAsync(connection, tx, "select set_config(@key, @value, true)", ct,
            ("key", SessionVariables.TenantId),
            ("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return tx;
    }

    private const string AccountColumns =
        "select id, email::text, password_hash, status, account_kind, locale, access_failed_count, lockout_end, lockout_enabled from identity.app_user";

    private static async Task<Account?> FindByEmailAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return await ReadAccountAsync(connection, tx,
            AccountColumns + " where normalized_email = upper(btrim(@key)) and deleted_at is null for update", email, ct).ConfigureAwait(false);
    }

    private static Task<Account?> FindByIdAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, CancellationToken ct) =>
        ReadAccountAsync(connection, tx, AccountColumns + " where id = @key and deleted_at is null for update", id, ct);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both statements are constants in this class; the key is a bound parameter.")]
    private static async Task<Account?> ReadAccountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, object key, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(sql, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("key", key));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Account(
            reader.GetGuid(0),
            await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetString(5),
            reader.GetInt32(6),
            await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(7, ct).ConfigureAwait(false),
            reader.GetBoolean(8));
    }

    private static async Task<StoredToken?> ReadTokenAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, CancellationToken ct, bool forUpdate = false)
    {
        string? value = await ScalarAsync<string?>(connection, tx,
            "select value from identity.app_user_token where user_id = @id and login_provider = @provider and name = @name"
            + (forUpdate ? " for update" : ""),
            ct, ("id", userId), ("provider", TokenProvider), ("name", ResetTokenName)).ConfigureAwait(false);

        string[] parts = value?.Split('|') ?? [];
        if (parts.Length != 3
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long created))
        {
            return null;
        }

        return new StoredToken(Convert.FromHexString(parts[0]), DateTimeOffset.FromUnixTimeSeconds(expires), DateTimeOffset.FromUnixTimeSeconds(created));
    }

    private static bool TryDecode(string? token, out Guid userId, out byte[] secret)
    {
        userId = Guid.Empty;
        secret = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return false;
        }

        try
        {
            string padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            byte[] bytes = Convert.FromBase64String(padded);
            if (bytes.Length != 16 + RandomBytes)
            {
                return false;
            }

            userId = new Guid(bytes.AsSpan(0, 16), bigEndian: true);
            secret = bytes[16..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every statement is a constant in this class; values are bound parameters.")]
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, tx);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? default! : (T)result;
    }
}
