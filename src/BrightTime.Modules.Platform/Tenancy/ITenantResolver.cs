using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Platform.Tenancy;

/// <summary>The tenant a host maps to, plus the facts every request needs about it.</summary>
public sealed record ResolvedTenant(
    Guid TenantId,
    string Slug,
    string DisplayName,
    string RegionName,
    TimeZoneInfo TimeZone,
    Locale DefaultLocale,
    IReadOnlyList<Locale> SupportedLocales,
    string StudentPrivacyPack,
    short MinCellSize,
    bool IsActive);

/// <summary>
/// Maps an incoming host to a tenant.
/// </summary>
/// <remarks>
/// Runs before anything else touches the database, because
/// <see cref="Infrastructure.Persistence.TenantSessionInterceptor"/> throws
/// rather than let a query run with no tenant. Results are cached: this is on
/// the path of every request including anonymous search, and it must not be a
/// query per request.
/// </remarks>
public interface ITenantResolver
{
    Task<ResolvedTenant?> ResolveByHostAsync(string host, CancellationToken cancellationToken = default);

    Task<ResolvedTenant?> ResolveBySlugAsync(string slug, CancellationToken cancellationToken = default);
}
