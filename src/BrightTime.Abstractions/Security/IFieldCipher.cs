using BrightTime.SharedKernel.Ids;

namespace BrightTime.Abstractions.Security;

/// <summary>
/// Field-level encryption for the <c>*_enc bytea</c> columns.
/// </summary>
/// <remarks>
/// <para>
/// Encryption happens in the application, not in the database. pgcrypto would
/// put key material in query text, in <c>pg_stat_statements</c> and in the
/// WAL; this way the database never sees a key, and a stolen basebackup is
/// inert for the encrypted columns.
/// </para>
/// <para>
/// AES-256-GCM with a per-tenant data key wrapped by a KEK in
/// <see cref="IKeyVault"/>. The tenant id is bound into the additional
/// authenticated data, so a ciphertext copied from one tenant's row into
/// another's fails to decrypt rather than silently succeeding.
/// </para>
/// </remarks>
public interface IFieldCipher
{
    /// <summary>Encrypts a value for a tenant. Returns null for null input, so an optional column stays optional.</summary>
    ValueTask<byte[]?> EncryptAsync(TenantId tenant, string? plaintext, CancellationToken cancellationToken = default);

    /// <summary>Decrypts a value. Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the ciphertext was not produced for this tenant.</summary>
    ValueTask<string?> DecryptAsync(TenantId tenant, byte[]? ciphertext, CancellationToken cancellationToken = default);
}
