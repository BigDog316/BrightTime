using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BrightTime.Hosting.Tenancy;

/// <summary>
/// The current tenant's address built the way the request hosts build it (D91): its primary
/// domain with <c>Tenancy:PublicScheme</c> and <c>Tenancy:PublicPort</c>, or <c>Auth:PublicBaseUrl</c>
/// for a single-site install. Never from anything a request said.
/// </summary>
public sealed class TenantSiteAddress(ITenantContext tenant, ITenantLookup lookup, IOptions<TenancyOptions> tenancy, IConfiguration configuration) : ISiteAddress
{
    public async Task<Uri> BaseUrlAsync(CancellationToken cancellationToken = default)
    {
        if (configuration.GetValue<Uri?>("Auth:PublicBaseUrl") is { IsAbsoluteUri: true } fixedUrl)
        {
            return fixedUrl;
        }

        TenantSite site = await lookup.FindByIdAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {tenant.TenantId} has no site.");
        return HostTenancy.BaseUrlFor(site, tenancy.Value);
    }
}

public static class SiteAddressRegistration
{
    /// <summary>
    /// The site address for a host with no request to take it from (the worker). Needs an
    /// <see cref="NpgsqlDataSource"/>. Outside Development the scheme must be https, as the request
    /// hosts insist: a badge names the site in what it signs.
    /// </summary>
    public static IServiceCollection AddSiteAddress(this IServiceCollection services, IConfiguration configuration, bool development)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        TenancyOptions tenancy = configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new();
        if (tenancy.PublicScheme != Uri.UriSchemeHttps && !development)
        {
            throw new InvalidOperationException("Tenancy:PublicScheme must be https outside Development: badges name the site's address.");
        }

        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.SectionName));
        services.TryAddSingleton(sp => new SqlTenantLookup(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ITenantLookup>(sp => new CachedTenantLookup(sp.GetRequiredService<SqlTenantLookup>()));
        services.AddScoped<ISiteAddress, TenantSiteAddress>();
        return services;
    }
}
