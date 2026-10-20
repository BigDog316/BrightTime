using System.Security.Cryptography;
using BrightTime.Infrastructure.Security;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.Configuration;

namespace BrightTime.Infrastructure.Tests;

public sealed class DerivedTenantDataKeyProviderTests
{
    private static readonly TenantId First = new(Guid.Parse("0196c3e8-0000-7000-8000-000000000a01"));
    private static readonly TenantId Second = new(Guid.Parse("0196c3e8-0000-7000-8000-000000000a02"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (DerivedTenantDataKeyProvider Keys, AesGcmFieldCipher Cipher) Build(Dictionary<string, string?> settings, bool development = false)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var vault = new ConfiguredKeyVault(configuration, development);
        List<DataKeyVersion> versions = configuration.GetSection(DerivedTenantDataKeyProvider.SectionName).Get<List<DataKeyVersion>>() ?? [];
        var keys = new DerivedTenantDataKeyProvider(vault, versions);
        return (keys, new AesGcmFieldCipher(keys));
    }

    private static Dictionary<string, string?> TwoVersions() => new()
    {
        ["Security:DataKeys:0:Version"] = "1",
        ["Security:DataKeys:0:KeyRef"] = "master-1",
        ["Security:DataKeys:1:Version"] = "2",
        ["Security:DataKeys:1:KeyRef"] = "master-2",
        ["Security:Keys:master-1"] = Convert.ToBase64String(new byte[32]),
        ["Security:Keys:master-2"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
    };

    [Fact]
    public async Task The_newest_version_encrypts_and_every_held_version_still_decrypts()
    {
        (DerivedTenantDataKeyProvider keys, AesGcmFieldCipher cipher) = Build(TwoVersions());

        keys.CurrentVersion.ShouldBe((short)2);
        (await keys.GetCurrentAsync(First, Ct)).Version.ShouldBe((short)2);

        byte[]? sealedValue = await cipher.EncryptAsync(First, "a push token", Ct);
        (await cipher.DecryptAsync(First, sealedValue, Ct)).ShouldBe("a push token");
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_key_and_cannot_read_anothers()
    {
        (_, AesGcmFieldCipher cipher) = Build(TwoVersions());

        byte[]? sealedValue = await cipher.EncryptAsync(First, "a push token", Ct);

        await Should.ThrowAsync<CryptographicException>(async () => await cipher.DecryptAsync(Second, sealedValue, Ct));
    }

    [Fact]
    public async Task The_same_master_key_gives_the_same_tenant_key_in_every_process()
    {
        // Derived, not random: two hosts, or one after a restart, read each other's ciphertext.
        (_, AesGcmFieldCipher api) = Build(TwoVersions());
        (_, AesGcmFieldCipher jobs) = Build(TwoVersions());

        byte[]? sealedValue = await api.EncryptAsync(First, "a push token", Ct);

        (await jobs.DecryptAsync(First, sealedValue, Ct)).ShouldBe("a push token");
    }

    [Fact]
    public async Task A_missing_key_is_an_error_outside_development()
    {
        Dictionary<string, string?> settings = TwoVersions();
        settings.Remove("Security:Keys:master-2");
        (_, AesGcmFieldCipher cipher) = Build(settings);

        await Should.ThrowAsync<InvalidOperationException>(async () => await cipher.EncryptAsync(First, "x", Ct));

        // Development makes one up instead, for the life of the process.
        (_, AesGcmFieldCipher local) = Build(settings, development: true);
        (await local.DecryptAsync(First, await local.EncryptAsync(First, "x", Ct), Ct)).ShouldBe("x");
    }

    [Fact]
    public void A_version_without_a_reference_is_refused_at_start()
    {
        Should.Throw<InvalidOperationException>(() => new DerivedTenantDataKeyProvider(
            new ConfiguredKeyVault(new ConfigurationBuilder().Build(), development: true),
            [new DataKeyVersion { Version = 1, KeyRef = "" }]));
    }
}
