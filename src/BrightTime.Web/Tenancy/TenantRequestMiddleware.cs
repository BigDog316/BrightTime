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
/// Resolves the request's tenant before anything reads it: from its host, from configuration,
/// or (on sample data) the fictional tenant.
/// </summary>
/// <remarks>
/// <para>By host:</para>
/// <list type="bullet">
/// <item>an unknown host is a 404 that names no tenant;</item>
/// <item>a tenant that is not active (provisioning, suspended, archived) is a 503;</item>
/// <item>a tenant's other domains redirect permanently to its primary one, so a tenant's real
/// traffic runs on one host, which is what a passkey's relying party needs.</item>
/// </list>
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

        if (finderOptions.Value.Mode == FinderMode.Sample)
        {
            // Sample data has one fictional tenant and no database to ask.
            site.Set(null, authOptions.Value.PublicBaseUrl);
            tenant.SetTenant(new TenantId(KettleCatalogue.TenantId), Zone(finderOptions.Value.TimeZone), Locale.Default);
            await next(context).ConfigureAwait(false);
            return;
        }

        ITenantLookup lookup = context.RequestServices.GetRequiredService<ITenantLookup>();
        TenancyOptions tenancy = tenancyOptions.Value;
        TenantSite? found;
        Uri? baseUrl;

        if (tenancy.Mode == TenancyMode.Fixed)
        {
            found = await lookup.FindByIdAsync(new TenantId(tenancy.TenantId), context.RequestAborted).ConfigureAwait(false);
            baseUrl = authOptions.Value.PublicBaseUrl;
            if (found is null)
            {
                await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "This site is not set up yet.").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            string? host = HostNames.Normalise(context.Request.Host.Value);
            HostResolution? resolved = host is null ? null : await lookup.FindByHostAsync(host, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                await RefuseAsync(context, StatusCodes.Status404NotFound, "No BrightTime site is configured at this address.").ConfigureAwait(false);
                return;
            }

            found = resolved.Site;
            baseUrl = BaseUrlFor(found, tenancy);
            if (!resolved.IsPrimaryHost && found.IsActive)
            {
                // One address per tenant. 308 keeps the method, so a form posted to an alias still lands.
                context.Response.Redirect(new Uri(baseUrl, context.Request.PathBase + context.Request.Path + context.Request.QueryString).AbsoluteUri, permanent: true, preserveMethod: true);
                return;
            }
        }

        if (!found.IsActive)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "This site is not available at the moment.").ConfigureAwait(false);
            return;
        }

        site.Set(found, baseUrl);
        Locale fallback = Locale.TryFromCode(found.DefaultLocale, out Locale tenantDefault) ? tenantDefault : Locale.Default;
        tenant.SetTenant(found.TenantId, Zone(found.TimeZone), fallback);

        await next(context).ConfigureAwait(false);
    }

    /// <summary>A tenant's own address, from its primary domain: never from the request.</summary>
    public static Uri BaseUrlFor(TenantSite site, TenancyOptions tenancy)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(tenancy);
        return new UriBuilder(tenancy.PublicScheme, site.PrimaryHost, tenancy.PublicPort ?? -1, "/").Uri;
    }

    private static async Task RefuseAsync(HttpContext context, int status, string message)
    {
        // With a body, so the status-code pages do not re-run it as a tenant page.
        if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
    }

    private static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
