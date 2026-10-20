using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>
/// One-time codes that carry a sign-in finished in the browser back to the app (D41), as RFC 8252
/// has native apps do it: the web signs the person in with their provider, issues a code bound to
/// the app's PKCE challenge, and hands it to the app's own scheme; the API redeems it for tokens.
/// </summary>
/// <remarks>
/// <para>
/// The code alone is worth nothing: redeeming it needs the PKCE verifier that only the app which
/// started the flow holds (RFC 7636, S256), so another app that claims the scheme and catches the
/// redirect gets nothing it can use.
/// </para>
/// <para>
/// 256 random bits, kept as a SHA-256 hash in <c>app_user_token</c> with the challenge and an
/// expiry two minutes out, one per account, spent on first use whether or not it succeeds.
/// </para>
/// </remarks>
public sealed class AppSignInCodes(NpgsqlDataSource authDataSource, IClock clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private const string TokenProvider = "brighttime";
    private const string TokenName = "app_sign_in_code";
    private const int SecretBytes = 32;

    /// <summary>Whether a value has the shape of an S256 challenge: base64url SHA-256, 43 characters.</summary>
    public static bool IsChallenge(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A code for <paramref name="user"/>, redeemable once by the holder of the challenge's verifier.</summary>
    public async Task<string> IssueAsync(TenantId tenant, UserId user, string method, string codeChallenge, CancellationToken cancellationToken = default)
    {
        if (!IsChallenge(codeChallenge))
        {
            throw new ArgumentException("An S256 code challenge.", nameof(codeChallenge));
        }

        byte[] secret = RandomNumberGenerator.GetBytes(SecretBytes);
        string stored = string.Join('|',
            Convert.ToHexString(SHA256.HashData(secret)),
            (clock.UtcNow + Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            codeChallenge,
            method);

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand upsert = new(
            """
            insert into identity.app_user_token (user_id, login_provider, name, value)
            values (@id, @provider, @name, @value)
            on conflict (user_id, login_provider, name) do update set value = excluded.value
            """, connection, tx))
        {
            upsert.Parameters.Add(new NpgsqlParameter("id", user.Value));
            upsert.Parameters.Add(new NpgsqlParameter("provider", TokenProvider));
            upsert.Parameters.Add(new NpgsqlParameter("name", TokenName));
            upsert.Parameters.Add(new NpgsqlParameter("value", stored));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Base64Url([.. user.Value.ToByteArray(bigEndian: true), .. secret]);
    }

    /// <summary>
    /// The account and how it signed in, when the code is current and the verifier matches its
    /// challenge; null for anything else. The code is spent either way.
    /// </summary>
    public async Task<(UserId User, string Method)?> RedeemAsync(TenantId tenant, string? code, string? verifier, CancellationToken cancellationToken = default)
    {
        if (!TryDecode(code, out Guid userId, out byte[] secret) || verifier is not { Length: >= 43 and <= 128 })
        {
            return null;
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        string? value;
        await using (NpgsqlCommand read = new(
            """
            delete from identity.app_user_token where user_id = @id and login_provider = @provider and name = @name
            returning value
            """, connection, tx))
        {
            read.Parameters.Add(new NpgsqlParameter("id", userId));
            read.Parameters.Add(new NpgsqlParameter("provider", TokenProvider));
            read.Parameters.Add(new NpgsqlParameter("name", TokenName));
            value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        string[] parts = value?.Split('|', 4) ?? [];
        if (parts.Length != 4
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
            || DateTimeOffset.FromUnixTimeSeconds(expires) <= clock.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[0]), SHA256.HashData(secret)))
        {
            return null;
        }

        // RFC 7636's S256: the challenge is the base64url SHA-256 of the verifier.
        byte[] challenge = Encoding.ASCII.GetBytes(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        return CryptographicOperations.FixedTimeEquals(challenge, Encoding.ASCII.GetBytes(parts[2]))
            ? (new UserId(userId), parts[3])
            : null;
    }

    private static bool TryDecode(string? code, out Guid userId, out byte[] secret)
    {
        userId = Guid.Empty;
        secret = [];
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100)
        {
            return false;
        }

        try
        {
            string padded = code.Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
            if (bytes.Length != 16 + SecretBytes)
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

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
