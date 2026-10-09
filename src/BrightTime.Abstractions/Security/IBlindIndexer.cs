using BrightTime.SharedKernel.Ids;

namespace BrightTime.Abstractions.Security;

/// <summary>
/// Produces the deterministic lookup token for an encrypted column — the
/// <c>*_blind_index bytea</c> columns.
/// </summary>
/// <remarks>
/// Keyed HMAC-SHA256, not a bare digest and not deterministic encryption. A
/// bare <c>sha256(phone)</c> is a dictionary attack against a ten-digit
/// keyspace; an HMAC under a key the database never holds is not. Rotation is
/// a reindex job, which is why <c>platform.blind_index_key</c> carries a
/// version.
/// </remarks>
public interface IBlindIndexer
{
    ValueTask<byte[]> ComputeAsync(TenantId tenant, string value, CancellationToken cancellationToken = default);

    /// <summary>The key version the token was computed under, written alongside it so rotation can proceed incrementally.</summary>
    short CurrentKeyVersion { get; }
}
