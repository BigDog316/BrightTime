using System.Security.Cryptography;
using System.Text;
using BrightTime.Abstractions.Security;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Infrastructure.Security;

/// <summary>One version of the master key tenant data keys are derived from.</summary>
public sealed class DataKeyVersion
{
    public short Version { get; set; }

    /// <summary>The vault reference of the master key. The key itself never appears in configuration that is logged.</summary>
    public string KeyRef { get; set; } = string.Empty;
}

/// <summary>
/// Per-tenant data keys derived with HKDF-SHA256 from a master key held in the vault: the tenant
/// id is the salt and the version is in the info, so each tenant and each version gets its own
/// 256-bit key and none is ever stored.
/// </summary>
/// <remarks>
/// <para>
/// <c>Security:DataKeys</c> lists the versions held, each with its vault reference. The highest
/// encrypts; every listed version still decrypts, so rotation adds a version and a background job
/// re-encrypts at its own pace (see <see cref="ITenantDataKeyProvider"/>).
/// </para>
/// <para>
/// Derivation rather than a wrapped key per tenant: there is nothing to provision when a tenant
/// is created, and the vault holds one secret per version. Destroying a tenant's data by
/// destroying its key, which wrapped keys allow, would need a key row per tenant; when
/// offboarding demands it, that provider can replace this one behind the same interface.
/// </para>
/// </remarks>
public sealed class DerivedTenantDataKeyProvider : ITenantDataKeyProvider
{
    public const string SectionName = "Security:DataKeys";

    private readonly IKeyVault _vault;
    private readonly Dictionary<short, string> _refs;
    private readonly short _current;

    public DerivedTenantDataKeyProvider(IKeyVault vault, IReadOnlyList<DataKeyVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count == 0 || versions.Any(v => v.Version <= 0 || string.IsNullOrWhiteSpace(v.KeyRef)))
        {
            throw new InvalidOperationException($"{SectionName} needs at least one version, each with a positive Version and a KeyRef.");
        }

        _vault = vault;
        _refs = versions.ToDictionary(v => v.Version, v => v.KeyRef);
        _current = versions.Max(v => v.Version);
    }

    public short CurrentVersion => _current;

    public ValueTask<TenantDataKey> GetCurrentAsync(TenantId tenant, CancellationToken cancellationToken = default) =>
        GetByVersionAsync(tenant, _current, cancellationToken);

    public async ValueTask<TenantDataKey> GetByVersionAsync(TenantId tenant, short version, CancellationToken cancellationToken = default)
    {
        if (!_refs.TryGetValue(version, out string? keyRef))
        {
            throw new CryptographicException($"Data key version {version} is no longer held.");
        }

        ReadOnlyMemory<byte> master = await _vault.GetSymmetricKeyAsync(keyRef, cancellationToken).ConfigureAwait(false);
        byte[] derived = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            master.Span.ToArray(),
            outputLength: 32,
            salt: tenant.Value.ToByteArray(),
            info: Encoding.UTF8.GetBytes($"brighttime.tenant-data-key.v{version}"));
        try
        {
            return new TenantDataKey(version, derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }
}

/// <summary>Registration of field encryption and blind indexing for a host.</summary>
public static class FieldEncryption
{
    /// <summary>
    /// The vault, the tenant data keys and the cipher and blind indexer over them. Without
    /// <c>Security:DataKeys</c>, Development gets version 1 under a made-up key; anywhere else
    /// resolving a cipher fails, which is when it matters.
    /// </summary>
    public static IServiceCollection AddFieldEncryption(this IServiceCollection services, IConfiguration configuration, bool development)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IKeyVault>(_ => new ConfiguredKeyVault(configuration, development));
        services.AddSingleton(sp =>
        {
            List<DataKeyVersion> versions = configuration.GetSection(DerivedTenantDataKeyProvider.SectionName).Get<List<DataKeyVersion>>() ?? [];
            if (versions.Count == 0 && development)
            {
                versions.Add(new DataKeyVersion { Version = 1, KeyRef = "development-data-key-1" });
            }

            return new DerivedTenantDataKeyProvider(sp.GetRequiredService<IKeyVault>(), versions);
        });
        services.AddSingleton<ITenantDataKeyProvider>(sp => sp.GetRequiredService<DerivedTenantDataKeyProvider>());
        services.AddSingleton<IFieldCipher>(sp => new AesGcmFieldCipher(sp.GetRequiredService<ITenantDataKeyProvider>()));
        services.AddSingleton<IBlindIndexer>(sp => new HmacBlindIndexer(
            sp.GetRequiredService<ITenantDataKeyProvider>(), sp.GetRequiredService<DerivedTenantDataKeyProvider>().CurrentVersion));
        services.AddSingleton(sp => new HmacTokenHasher(sp.GetRequiredService<ITenantDataKeyProvider>()));
        return services;
    }
}
