using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Security;

/// <summary>A versioned 256-bit data key for one tenant.</summary>
public sealed class TenantDataKey
{
    private readonly byte[] _key;

    public TenantDataKey(short version, ReadOnlyMemory<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("A field-encryption data key must be 256 bits.", nameof(key));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Key versions start at 1.");
        }

        Version = version;
        _key = key.ToArray();
    }

    public short Version { get; }

    internal ReadOnlySpan<byte> Span => _key;
}

/// <summary>
/// Resolves a tenant's data key, unwrapping it from the KEK held in
/// <see cref="Abstractions.Security.IKeyVault"/>.
/// </summary>
/// <remarks>
/// Per-tenant keys mean a key compromise is scoped to one region, and a
/// tenant offboarding can be completed by destroying a key rather than by
/// trusting a delete to have reached every backup. Versioning means rotation
/// does not require rewriting every row at once: new writes use the current
/// version, reads accept any version still held, and a background job
/// re-encrypts.
/// </remarks>
public interface ITenantDataKeyProvider
{
    ValueTask<TenantDataKey> GetCurrentAsync(TenantId tenant, CancellationToken cancellationToken = default);

    ValueTask<TenantDataKey> GetByVersionAsync(TenantId tenant, short version, CancellationToken cancellationToken = default);
}
