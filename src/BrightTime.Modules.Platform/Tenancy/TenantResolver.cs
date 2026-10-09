using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Platform.Tenancy;

/// <summary>
/// Resolves a tenant from <c>platform.tenant_domain</c>, cached.
/// </summary>
/// <remarks>
/// <para>
/// This is the one query in the system that runs without tenant context, which
/// is why <c>tenant_domain_lookup</c> exists as a separate RLS policy and why
/// the table holds nothing but a host and a tenant id.
/// </para>
/// <para>
/// Reads go through <see cref="DbContext.Database"/> with an explicit
/// transaction suppressed: the session interceptor would otherwise demand a
/// tenant that has not been resolved yet. The chicken-and-egg is unavoidable;
/// keeping it to one table with no personal data is the mitigation.
/// </para>
/// </remarks>
public sealed class TenantResolver(
    IDbContextFactory<PlatformDbContext> contextFactory,
    HybridCache cache,
    ILogger<TenantResolver> logger) : ITenantResolver
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };

    public async Task<ResolvedTenant?> ResolveByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string normalised = NormaliseHost(host);

        return await cache.GetOrCreateAsync(
            $"tenant:host:{normalised}",
            normalised,
            async (key, ct) => await LoadByHostAsync(key, ct).ConfigureAwait(false),
            CacheOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        async ValueTask<ResolvedTenant?> LoadByHostAsync(string h, CancellationToken ct)
        {
            await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            Guid tenantId = await db.TenantDomains
                .AsNoTracking()
                .Where(d => d.Host == h)
                .Select(d => d.TenantId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (tenantId == Guid.Empty)
            {
                logger.UnknownHost(h);
                return null;
            }

            return await LoadAsync(db, tenantId, ct).ConfigureAwait(false);
        }
    }

    public async Task<ResolvedTenant?> ResolveBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return await cache.GetOrCreateAsync(
            $"tenant:slug:{slug.ToLowerInvariant()}",
            slug,
            async (s, ct) =>
            {
                await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                Guid tenantId = await db.Tenants
                    .AsNoTracking()
                    .Where(t => t.Slug == s)
                    .Select(t => t.Id)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                return tenantId == Guid.Empty
                    ? null
                    : await LoadAsync(db, tenantId, ct).ConfigureAwait(false);
            },
            CacheOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResolvedTenant?> LoadAsync(PlatformDbContext db, Guid tenantId, CancellationToken ct)
    {
        Tenant? tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            .ConfigureAwait(false);

        if (tenant is null)
        {
            return null;
        }

        List<TenantLocale> locales = await db.TenantLocales
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<Locale> supported = [];
        foreach (TenantLocale row in locales)
        {
            if (Locale.TryFromCode(row.Locale, out Locale locale))
            {
                supported.Add(locale);
            }
        }

        if (supported.Count == 0)
        {
            supported.Add(Locale.Default);
        }

        Locale defaultLocale = Locale.Resolve(tenant.DefaultLocale);

        return new ResolvedTenant(
            tenant.Id,
            tenant.Slug,
            tenant.DisplayName,
            tenant.RegionName,
            ResolveTimeZoneOrUtc(tenant.Timezone),
            defaultLocale,
            supported,
            tenant.StudentPrivacyPack,
            tenant.MinCellSize,
            tenant.IsActive);
    }

    private static TimeZoneInfo ResolveTimeZoneOrUtc(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // A bad timezone must not take the tenant offline; it degrades
            // "due today" to UTC and shows up in the logs.
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    internal static string NormaliseHost(string host)
    {
        int colon = host.IndexOf(':', StringComparison.Ordinal);
        string withoutPort = colon >= 0 ? host[..colon] : host;
        return withoutPort.Trim().TrimEnd('.').ToLowerInvariant();
    }
}

internal static partial class TenantResolverLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "No tenant is mapped to host {Host}.")]
    public static partial void UnknownHost(this ILogger logger, string host);
}
