namespace BrightTime.Abstractions.Security;

/// <summary>
/// Resolves key material from a reference. The reference is what gets stored
/// (<c>issuer_key.private_key_ref</c>, <c>blind_index_key.key_ref</c>); the
/// key itself never lands in a column, a migration or a backup.
/// </summary>
public interface IKeyVault
{
    /// <summary>Fetches a symmetric key by reference.</summary>
    ValueTask<ReadOnlyMemory<byte>> GetSymmetricKeyAsync(string keyRef, CancellationToken cancellationToken = default);

    /// <summary>Signs a payload with a referenced private key, without the key leaving the vault boundary where the provider supports it.</summary>
    ValueTask<byte[]> SignAsync(string keyRef, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
