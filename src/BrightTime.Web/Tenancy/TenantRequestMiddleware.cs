using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Auth;
using BrightTime.Web.Composition;
using Microsoft.Extensions.Options;

namespace BrightTime.Web.Tenancy;

/// <summary>
/// Resolves the request's tenant before anything reads it: on sample data the fictional
/// tenant, otherwise as <see cref="HostTenancy"/> does for every host.
/// </summary>
/// <remarks>
/// <para>
/// The tenant's default locale is set here, and <see cref="Localisation.LocaleMiddleware"/>
/// settles the request's own once the account is known.
/// </para>
/// </remarks>
public sealed class TenantRequestMiddleware(
    RequestDelegate next,
    IOptions<FinderOptions> finderOptions,
    IOptions<TenancyOptions> tenancyOptions,
    IOptions<AuthOptions> authOptions)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, SiteContext site)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(site);

        if (HostTenancy.IsHealthProbe(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (finderOptions.Value.Mode == FinderMode.Sample)
        {
            // Sample data has one fictional tenant and no database to ask.
            site.Set(null, authOptions.Value.PublicBaseUrl);
            tenant.SetTenant(new TenantId(KettleCatalogue.TenantId), Zone(finderOptions.Value.TimeZone), Locale.Default);
            await next(context).ConfigureAwait(false);
            return;
        }

        await HostTenancy.HandleAsync(
            context, tenant, site, context.RequestServices.GetRequiredService<ITenantLookup>(),
            tenancyOptions.Value, authOptions.Value.PublicBaseUrl, next).ConfigureAwait(false);
    }

    /// <summary>A tenant's own address, from its primary domain: never from the request.</summary>
    public static Uri BaseUrlFor(TenantSite site, TenancyOptions tenancy) => HostTenancy.BaseUrlFor(site, tenancy);

    private static TimeZoneInfo Zone(string id) => HostTenancy.Zone(id);
}
