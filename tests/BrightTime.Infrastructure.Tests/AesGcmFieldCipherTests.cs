using System.Security.Cryptography;
using System.Text;
using BrightTime.Infrastructure.Security;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Tests;

public sealed class AesGcmFieldCipherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TenantId TenantA = new(Guid.Parse("0196c3e8-0000-7000-8000-00000000000a"));
    private static readonly TenantId TenantB = new(Guid.Parse("0196c3e8-0000-7000-8000-00000000000b"));

    [Theory]
    [InlineData("Marisol Rivera")]
    [InlineData("2011-09-23")]
    [InlineData("+1 914 555 0134")]
    [InlineData("শিক্ষার্থী")]
    [InlineData("")]
    public async Task Round_trips(string plaintext)
    {
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        byte[]? encrypted = await cipher.EncryptAsync(TenantA, plaintext, Ct);
        encrypted.ShouldNotBeNull();

        (await cipher.DecryptAsync(TenantA, encrypted, Ct)).ShouldBe(plaintext);
    }

    [Fact]
    public async Task Null_stays_null_so_an_optional_column_stays_optional()
    {
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        (await cipher.EncryptAsync(TenantA, null, Ct)).ShouldBeNull();
        (await cipher.DecryptAsync(TenantA, null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_same_plaintext_encrypts_differently_every_time()
    {
        // A fresh nonce per operation. Without it, equal ciphertexts would leak
        // which two family members share a surname, or which applicants gave
        // the same answer.
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        byte[]? first = await cipher.EncryptAsync(TenantA, "Rivera", Ct);
        byte[]? second = await cipher.EncryptAsync(TenantA, "Rivera", Ct);

        first.ShouldNotBe(second);
    }

    [Fact]
    public async Task A_ciphertext_from_another_tenant_does_not_decrypt()
    {
        // The tenant id is bound into the AAD. A row copied across tenants —
        // by a bad backfill, a restore into the wrong database, or a bug in a
        // cross-tenant query — fails loudly instead of yielding a plausible
        // value under the wrong tenant's eyes.
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        byte[]? encrypted = await cipher.EncryptAsync(TenantA, "Marisol Rivera", Ct);

        await Should.ThrowAsync<CryptographicException>(
            async () => await cipher.DecryptAsync(TenantB, encrypted, Ct));
    }

    [Fact]
    public async Task A_tampered_ciphertext_does_not_decrypt()
    {
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        byte[] encrypted = (await cipher.EncryptAsync(TenantA, "Marisol Rivera", Ct))!;
        encrypted[^1] ^= 0xFF;

        await Should.ThrowAsync<CryptographicException>(
            async () => await cipher.DecryptAsync(TenantA, encrypted, Ct));
    }

    [Fact]
    public async Task A_truncated_ciphertext_is_rejected_with_a_clear_error()
    {
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        CryptographicException exception = await Should.ThrowAsync<CryptographicException>(
            async () => await cipher.DecryptAsync(TenantA, [0x01, 0x00, 0x01], Ct));

        exception.Message.ShouldContain("envelope");
    }

    [Fact]
    public async Task An_unknown_format_version_is_rejected()
    {
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider());

        byte[] encrypted = (await cipher.EncryptAsync(TenantA, "Rivera", Ct))!;
        encrypted[0] = 0x7F;

        CryptographicException exception = await Should.ThrowAsync<CryptographicException>(
            async () => await cipher.DecryptAsync(TenantA, encrypted, Ct));

        exception.Message.ShouldContain("version");
    }

    [Fact]
    public async Task A_value_written_under_an_old_key_version_still_reads_after_rotation()
    {
        // Rotation has to be incremental: new writes use the current version,
        // reads accept any version still held, and a background job
        // re-encrypts. A big-bang rewrite of every encrypted column is not an
        // option on a live system.
        using var oldCipher = new AesGcmFieldCipher(new TestKeyProvider(currentVersion: 1));
        byte[]? writtenBeforeRotation = await oldCipher.EncryptAsync(TenantA, "Marisol Rivera", Ct);

        using var rotated = new AesGcmFieldCipher(new TestKeyProvider(currentVersion: 2));

        (await rotated.DecryptAsync(TenantA, writtenBeforeRotation, Ct)).ShouldBe("Marisol Rivera");

        byte[]? writtenAfter = await rotated.EncryptAsync(TenantA, "Marisol Rivera", Ct);
        writtenAfter![1..3].ShouldBe(new byte[] { 0x00, 0x02 }, "new writes carry the new key version");
    }

    [Fact]
    public async Task The_envelope_carries_a_version_and_a_key_version_in_the_clear()
    {
        using var cipher = new AesGcmFieldCipher(new TestKeyProvider(currentVersion: 3));

        byte[] encrypted = (await cipher.EncryptAsync(TenantA, "x", Ct))!;

        encrypted[0].ShouldBe((byte)0x01, "format version");
        encrypted[1].ShouldBe((byte)0x00);
        encrypted[2].ShouldBe((byte)0x03, "key version, big-endian");
        encrypted.Length.ShouldBe(1 + 2 + 12 + Encoding.UTF8.GetByteCount("x") + 16);
    }
}
