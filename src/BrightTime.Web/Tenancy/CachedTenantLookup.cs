using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.Caching.Memory;

namespace BrightTime.Web.Tenancy;

/// <summary>
/// Tenant lookups, cached: every request resolves its tenant, and the answer rarely changes.
/// </summary>
/// <remarks>
/// <para>
/// Found tenants are kept for five minutes, so a suspension takes up to five minutes to reach
/// every instance. Unknown hosts are remembered for thirty seconds, and the cache holds a
/// bounded number of entries, so a stream of made-up Host headers costs one query each per
/// half-minute at most and cannot grow memory without limit.
/// </para>
/// </remarks>
public sealed class CachedTenantLookup(ITenantLookup inner) : ITenantLookup, IDisposable
{
    public static readonly TimeSpan FoundLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MissingLifetime = TimeSpan.FromSeconds(30);
    public const int MaxEntries = 10_000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    public async Task<HostResolution?> FindByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        string key = "host:" + host;
        if (_cache.TryGetValue(key, out Box<HostResolution>? cached) && cached is not null)
        {
            return cached.Value;
        }

        HostResolution? found = await inner.FindByHostAsync(host, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, new Box<HostResolution>(found), Entry(found is null));
        return found;
    }

    public async Task<TenantSite?> FindByIdAsync(TenantId tenant, CancellationToken cancellationToken = default)
    {
        string key = "id:" + tenant.Value.ToString("N");
        if (_cache.TryGetValue(key, out Box<TenantSite>? cached) && cached is not null)
        {
            return cached.Value;
        }

        TenantSite? found = await inner.FindByIdAsync(tenant, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, new Box<TenantSite>(found), Entry(found is null));
        return found;
    }

    public void Dispose() => _cache.Dispose();

    private static MemoryCacheEntryOptions Entry(bool missing) => new()
    {
        Size = 1,
        AbsoluteExpirationRelativeToNow = missing ? MissingLifetime : FoundLifetime,
    };

    /// <summary>Lets a miss be cached as a value distinct from "not in the cache".</summary>
    private sealed record Box<T>(T? Value) where T : class;
}
