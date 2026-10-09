using BrightTime.Infrastructure.Security;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Tests;

/// <summary>
/// Deterministic per-tenant keys for tests. Derives from the tenant id so that
/// two tenants genuinely get different keys — which is the property the
/// cross-tenant tests below depend on.
/// </summary>
internal sealed class TestKeyProvider(short currentVersion = 1) : ITenantDataKeyProvider
{
    public ValueTask<TenantDataKey> GetCurrentAsync(TenantId tenant, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Derive(tenant, currentVersion));

    public ValueTask<TenantDataKey> GetByVersionAsync(TenantId tenant, short version, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Derive(tenant, version));

    private static TenantDataKey Derive(TenantId tenant, short version)
    {
        byte[] seed = [.. tenant.Value.ToByteArray(), (byte)version];
        return new TenantDataKey(version, System.Security.Cryptography.SHA256.HashData(seed));
    }
}
