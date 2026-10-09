using System.Security.Cryptography;
using System.Text;
using BrightTime.Abstractions.Security;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 lookup tokens for encrypted columns, matching
/// <c>platform.blind_index()</c> in the schema.
/// </summary>
/// <remarks>
/// <para>
/// Normalisation has to be identical on both sides or the lookup silently
/// misses: trim, then lowercase with the invariant culture. The SQL function
/// does <c>lower(btrim(value))</c>, which is the same operation for the ASCII
/// digits and Latin letters these columns hold (phone numbers, EINs).
/// </para>
/// <para>
/// Why HMAC rather than a digest: a phone number has about 10^10 candidates,
/// so <c>sha256(phone)</c> is a few seconds of brute force. Under a key the
/// database never holds, the token is useless to anyone who only has the
/// table.
/// </para>
/// </remarks>
public sealed class HmacBlindIndexer(ITenantDataKeyProvider keys, short currentKeyVersion = 1) : IBlindIndexer
{
    public short CurrentKeyVersion { get; } = currentKeyVersion;

    public async ValueTask<byte[]> ComputeAsync(TenantId tenant, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        TenantDataKey key = await keys.GetByVersionAsync(tenant, CurrentKeyVersion, cancellationToken)
            .ConfigureAwait(false);

        string normalised = Normalise(value);
        byte[] bytes = Encoding.UTF8.GetBytes(normalised);
        try
        {
            return HMACSHA256.HashData(key.Span, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Must stay identical to <c>lower(btrim(p_value))</c> in <c>platform.blind_index</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Must match PostgreSQL lower() in platform.blind_index byte for byte. " +
                        "Upper-casing here would compute a different HMAC than the SQL side and every lookup would miss.")]
    internal static string Normalise(string value) => value.Trim().ToLowerInvariant();
}
