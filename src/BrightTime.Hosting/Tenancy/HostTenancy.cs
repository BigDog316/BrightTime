using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Hosting.Tenancy;

/// <summary>
/// Resolves the request's tenant before anything reads it: from its host, or from
/// configuration. Shared by the web and API hosts.
/// </summary>
/// <remarks>
/// <para>By host:</para>
/// <list type="bullet">
/// <item>an unknown host is a 404 that names no tenant;</item>
/// <item>a tenant that is not active (provisioning, suspended, archived) is a 503;</item>
/// <item>a tenant's other domains redirect permanently to its primary one, so a tenant's real
/// traffic runs on one host, which is what a passkey's relying party and a token's issuer need.</item>
/// </list>
/// <para>
/// Health probes (<c>/health/…</c>) are answered without a tenant: they come from the
/// orchestrator, addressed to the container rather than to any tenant's domain.
/// </para>
/// </remarks>
public static class HostTenancy
{
    /// <summary>The probe paths that need no tenant.</summary>
    public static readonly PathString HealthPath = "/health";

    public static bool IsHealthProbe(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the tenant and sets it on <paramref name="tenant"/> and <paramref name="site"/>, then
    /// calls <paramref name="next"/>; or answers the request itself (404, 503, or a 308 to the
    /// tenant's primary host) and does not. <paramref name="fixedBaseUrl"/> is a fixed tenant's
    /// address, from configuration: it has no domain to ask.
    /// </summary>
    public static async Task HandleAsync(
        HttpContext context,
        TenantContext tenant,
        SiteContext site,
        ITenantLookup lookup,
        TenancyOptions tenancy,
        Uri? fixedBaseUrl,
        RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(next);

        TenantSite? found;
        Uri? baseUrl;

        if (tenancy.Mode == TenancyMode.Fixed)
        {
            found = await lookup.FindByIdAsync(new TenantId(tenancy.TenantId), context.RequestAborted).ConfigureAwait(false);
            baseUrl = fixedBaseUrl;
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

    public static TimeZoneInfo Zone(string id)
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
}
