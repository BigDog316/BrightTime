using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Abstractions.Security;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field encryption with a per-tenant data key.
/// </summary>
/// <remarks>
/// <para>
/// Wire format, written to the <c>*_enc bytea</c> columns:
/// </para>
/// <code>
/// [0]      version      1 byte   currently 0x01
/// [1..2]   key version  2 bytes  big-endian, so rotation is incremental
/// [3..14]  nonce       12 bytes  random per operation
/// [15..n]  ciphertext
/// [n..]    tag         16 bytes
/// </code>
/// <para>
/// The tenant id goes into the additional authenticated data. A ciphertext
/// lifted from one tenant's row into another's therefore fails to decrypt
/// rather than decrypting to a plausible value — a copy-paste bug or a
/// cross-tenant write becomes a <see cref="CryptographicException"/> instead
/// of a privacy incident.
/// </para>
/// </remarks>
public sealed class AesGcmFieldCipher : IFieldCipher, IDisposable
{
    private const byte FormatVersion = 0x01;
    private const int VersionLength = 1;
    private const int KeyVersionLength = 2;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = VersionLength + KeyVersionLength + NonceLength;

    private readonly ITenantDataKeyProvider _keys;
    private readonly ConcurrentDictionary<(Guid Tenant, short Version), AesGcm> _ciphers = new();
    private bool _disposed;

    public AesGcmFieldCipher(ITenantDataKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    public async ValueTask<byte[]?> EncryptAsync(TenantId tenant, string? plaintext, CancellationToken cancellationToken = default)
    {
        if (plaintext is null)
        {
            return null;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        TenantDataKey key = await _keys.GetCurrentAsync(tenant, cancellationToken).ConfigureAwait(false);
        AesGcm cipher = GetCipher(tenant, key);

        int plaintextLength = Encoding.UTF8.GetByteCount(plaintext);
        byte[] output = new byte[HeaderLength + plaintextLength + TagLength];

        output[0] = FormatVersion;
        BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(VersionLength, KeyVersionLength), key.Version);

        Span<byte> nonce = output.AsSpan(VersionLength + KeyVersionLength, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            cipher.Encrypt(
                nonce,
                plaintextBytes,
                output.AsSpan(HeaderLength, plaintextLength),
                output.AsSpan(HeaderLength + plaintextLength, TagLength),
                BuildAssociatedData(tenant));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        return output;
    }

    public async ValueTask<string?> DecryptAsync(TenantId tenant, byte[]? ciphertext, CancellationToken cancellationToken = default)
    {
        if (ciphertext is null)
        {
            return null;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ciphertext.Length < HeaderLength + TagLength)
        {
            throw new CryptographicException("Encrypted field is shorter than the minimum envelope.");
        }

        if (ciphertext[0] != FormatVersion)
        {
            throw new CryptographicException($"Unsupported field-encryption format version {ciphertext[0]}.");
        }

        short keyVersion = BinaryPrimitives.ReadInt16BigEndian(ciphertext.AsSpan(VersionLength, KeyVersionLength));
        TenantDataKey key = await _keys.GetByVersionAsync(tenant, keyVersion, cancellationToken).ConfigureAwait(false);
        AesGcm cipher = GetCipher(tenant, key);

        int payloadLength = ciphertext.Length - HeaderLength - TagLength;
        byte[] plaintextBytes = new byte[payloadLength];
        try
        {
            cipher.Decrypt(
                ciphertext.AsSpan(VersionLength + KeyVersionLength, NonceLength),
                ciphertext.AsSpan(HeaderLength, payloadLength),
                ciphertext.AsSpan(HeaderLength + payloadLength, TagLength),
                plaintextBytes,
                BuildAssociatedData(tenant));

            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private AesGcm GetCipher(TenantId tenant, TenantDataKey key) =>
        _ciphers.GetOrAdd(
            (tenant.Value, key.Version),
            static (_, k) => new AesGcm(k.Span, TagLength),
            key);

    private static byte[] BuildAssociatedData(TenantId tenant) => tenant.Value.ToByteArray();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (AesGcm cipher in _ciphers.Values)
        {
            cipher.Dispose();
        }

        _ciphers.Clear();
        _disposed = true;
    }
}
