using System.Collections.Concurrent;
using System.Security.Cryptography;
using BrightTime.Abstractions.Security;
using Microsoft.Extensions.Configuration;

namespace BrightTime.Infrastructure.Security;

/// <summary>
/// An <see cref="IKeyVault"/> over configuration: <c>Security:Keys:&lt;ref&gt;</c> holds a base64
/// key of 32 bytes or more. For local development and single-server installs; production binds a
/// managed vault (Azure Key Vault, AWS KMS) behind the same interface, and the references stored
/// in the database do not change.
/// </summary>
/// <remarks>
/// In Development a reference with no configured key gets one made up for the life of the
/// process, as the audit hash key and the token signing keys do: anything encrypted under it is
/// unreadable after a restart, which is the right failure for a developer's machine. Anywhere else
/// a missing key is an error, never a silent fallback.
/// </remarks>
public sealed class ConfiguredKeyVault(IConfiguration configuration, bool development) : IKeyVault
{
    public const string SectionName = "Security:Keys";

    private readonly ConcurrentDictionary<string, byte[]> _madeUp = new(StringComparer.Ordinal);

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

    /// <summary>Not offered here: signing keys live with their own features (D28's token keys).</summary>
    public ValueTask<byte[]> SignAsync(string keyRef, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The configured vault holds symmetric keys only.");
}
