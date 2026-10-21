using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Ui.Shared.Attendance;

/// <summary>
/// Documents a stolen phone must not reveal, kept sealed (D52): AES-256-GCM, with the key in the
/// operating system's keychain or Keystore (<see cref="ISecretStore"/>) and only ciphertext in the
/// app's storage (<see cref="IBlobStore"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each document is <c>version (1) | nonce (12) | tag (16) | ciphertext</c>, with its own name as
/// associated data, so a blob moved to another name doesn't open. A document that won't open is
/// treated as absent, never as an error the person must deal with.
/// </para>
/// <para>
/// Wiping forgets the key first and deletes the blobs after: if the delete is interrupted, what
/// remains can't be read by anyone, this app included.
/// </para>
/// <para>
/// docs/plan/06 named SQLCipher. A roster pack and a queue of marks are a few kilobytes read and
/// written whole, so sealed documents give the same protection without a native database, and the
/// encryption is tested here rather than trusted to a binding.
/// </para>
/// </remarks>
public sealed class SealedStore(ISecretStore secrets, IBlobStore blobs)
{
    public const string KeyName = "brighttime.sealed_key";

    private const byte Version = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync<T>(string name, T document)
    {
        if (OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("The sealed store needs AES-GCM, which a browser host lacks.");
        }

        byte[] key = await KeyAsync(create: true).ConfigureAwait(false) ?? throw new InvalidOperationException("A key was just made.");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        byte[] sealedBytes = new byte[1 + 12 + 16 + plain.Length];
        sealedBytes[0] = Version;
        Span<byte> nonce = sealedBytes.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using AesGcm aes = new(key, 16);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(29), sealedBytes.AsSpan(13, 16), Encoding.UTF8.GetBytes(name));
        await blobs.WriteAsync(name, sealedBytes).ConfigureAwait(false);
    }

    /// <summary>The document, or default when there is none, the key is gone, or it won't open.</summary>
    public async Task<T?> LoadAsync<T>(string name)
    {
        if (OperatingSystem.IsBrowser())
        {
            return default;
        }

        byte[]? sealedBytes = await blobs.ReadAsync(name).ConfigureAwait(false);
        byte[]? key = await KeyAsync(create: false).ConfigureAwait(false);
        if (sealedBytes is not { Length: > 29 } || sealedBytes[0] != Version || key is null)
        {
            return default;
        }

        byte[] plain = new byte[sealedBytes.Length - 29];
        try
        {
            using AesGcm aes = new(key, 16);
            aes.Decrypt(sealedBytes.AsSpan(1, 12), sealedBytes.AsSpan(29), sealedBytes.AsSpan(13, 16), plain, Encoding.UTF8.GetBytes(name));
            return JsonSerializer.Deserialize<T>(plain, Json);
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or JsonException)
        {
            return default;
        }
    }

    /// <summary>Forgets the key, then deletes every sealed document.</summary>
    public async Task WipeAsync()
    {
        await secrets.RemoveAsync(KeyName).ConfigureAwait(false);
        await blobs.DeleteAllAsync().ConfigureAwait(false);
    }

    private async Task<byte[]?> KeyAsync(bool create)
    {
        if (await secrets.GetAsync(KeyName).ConfigureAwait(false) is { Length: > 0 } known)
        {
            return Convert.FromBase64String(known);
        }

        if (!create)
        {
            return null;
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        await secrets.SetAsync(KeyName, Convert.ToBase64String(key)).ConfigureAwait(false);
        return key;
    }
}
