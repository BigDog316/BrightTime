using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using BrightTime.Abstractions.Security;
using Microsoft.Extensions.Configuration;

namespace BrightTime.Infrastructure.Security;

/// <summary>
/// An <see cref="IKeyVault"/> over configuration, for local development and single-server
/// installs; production binds a managed vault (Azure Key Vault, AWS KMS) behind the same interface,
/// and the references stored in the database do not change.
/// </summary>
/// <remarks>
/// <para>
/// Symmetric keys: <c>Security:Keys:&lt;ref&gt;</c> holds a base64 key of 32 bytes or more.
/// </para>
/// <para>
/// Signing keys (D66): <c>Security:SigningKeys:&lt;ref&gt;</c> holds an RSA private key as PEM
/// (PKCS#8 or PKCS#1), 2048 bits or more. A small install that doesn't want a key per reference
/// sets <c>Security:SigningKeys:default</c>, which serves every signing reference without one of its
/// own. A managed vault would hold one key per reference.
/// </para>
/// <para>
/// In Development a reference with no configured key gets one made up for the life of the
/// process, as the audit hash key and the token signing keys do: anything encrypted or signed
/// under it is unreadable or unverifiable after a restart, which is the right failure for a
/// developer's machine. Anywhere else a missing key is an error, never a silent fallback.
/// </para>
/// </remarks>
public sealed class ConfiguredKeyVault(IConfiguration configuration, bool development) : IKeyVault, IDisposable
{
    public const string SectionName = "Security:Keys";
    public const string SigningSectionName = "Security:SigningKeys";
    public const string DefaultSigningKey = "default";

    private const int MinimumRsaBits = 2048;

    private readonly ConcurrentDictionary<string, byte[]> _madeUp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RSA> _signing = new(StringComparer.Ordinal);

    public ValueTask<ReadOnlyMemory<byte>> GetSymmetricKeyAsync(string keyRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
        string? configured = configuration[$"{SectionName}:{keyRef}"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            byte[] key = Convert.FromBase64String(configured);
            if (key.Length < 32)
            {
                throw new InvalidOperationException($"{SectionName}:{keyRef} must be a base64 key of 32 bytes or more.");
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(key);
        }

        if (!development)
        {
            throw new InvalidOperationException($"No key is configured for '{keyRef}' ({SectionName}:{keyRef}).");
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>>(_madeUp.GetOrAdd(keyRef, _ => RandomNumberGenerator.GetBytes(32)));
    }

    public ValueTask<byte[]> SignAsync(string keyRef, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Signing(keyRef).SignData(payload.Span, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    public ValueTask<string> GetPublicJwkAsync(string keyRef, CancellationToken cancellationToken = default)
    {
        RSAParameters key = Signing(keyRef).ExportParameters(includePrivateParameters: false);
        return ValueTask.FromResult(JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // RFC 7638's members, in its order, so a thumbprint of this text is the key's thumbprint.
            ["e"] = Base64Url(key.Exponent!),
            ["kty"] = "RSA",
            ["n"] = Base64Url(key.Modulus!),
        }));
    }

    public void Dispose()
    {
        foreach (RSA key in _signing.Values)
        {
            key.Dispose();
        }
    }

    private RSA Signing(string keyRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
        return _signing.GetOrAdd(keyRef, reference =>
        {
            string? pem = configuration[$"{SigningSectionName}:{reference}"] ?? configuration[$"{SigningSectionName}:{DefaultSigningKey}"];
            RSA rsa = RSA.Create();
            if (!string.IsNullOrWhiteSpace(pem))
            {
                rsa.ImportFromPem(pem);
            }
            else if (development)
            {
                rsa.KeySize = 3072;
            }
            else
            {
                rsa.Dispose();
                throw new InvalidOperationException($"No signing key is configured for '{reference}' ({SigningSectionName}:{reference} or {SigningSectionName}:{DefaultSigningKey}).");
            }

            if (rsa.KeySize < MinimumRsaBits)
            {
                rsa.Dispose();
                throw new InvalidOperationException($"{SigningSectionName}:{reference} must be an RSA key of {MinimumRsaBits} bits or more.");
            }

            return rsa;
        });
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
