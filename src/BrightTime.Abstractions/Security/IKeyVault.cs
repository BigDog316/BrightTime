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

    /// <summary>
    /// Signs a payload with a referenced private key, without the key leaving the vault boundary where
    /// the provider supports it. Signing keys are RSA, and the signature is RS256 (RSASSA-PKCS1-v1_5 with
    /// SHA-256), as Open Badges 3.0's JWT proofs require (D66).
    /// </summary>
    ValueTask<byte[]> SignAsync(string keyRef, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>The public half of a referenced signing key, as a JSON Web Key (RFC 7517) with no private members.</summary>
    ValueTask<string> GetPublicJwkAsync(string keyRef, CancellationToken cancellationToken = default);
}
