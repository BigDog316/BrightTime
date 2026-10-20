using System.Security.Claims;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Net.Http.Headers;

namespace BrightTime.Api;

/// <summary>
/// The request's language, from what the tenant offers: <c>?locale=</c> first (for widgets and
/// testing), then the signed-in person's own choice, then <c>Accept-Language</c>, then the
/// tenant's default. Every response says which it got in <c>Content-Language</c>.
/// </summary>
/// <remarks>
/// The web host also remembers an explicit choice in a cookie. An API client sends its choice
/// on every request instead, so nothing is stored here.
/// </remarks>
public sealed class ApiLocaleMiddleware(RequestDelegate next)
{
    public const string QueryParameter = "locale";

    public Task InvokeAsync(HttpContext context, TenantContext tenant, SiteContext site)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(site);

        if (tenant.IsResolved)
        {
            Locale locale = Choose(context, site.Site?.EnabledLocales ?? Locale.Supported, tenant.Locale);
            tenant.SetLocale(locale);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderNames.ContentLanguage] = locale.Code;
                context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptLanguage);
                return Task.CompletedTask;
            });
        }

        return next(context);
    }

    public static Locale Choose(HttpContext context, IReadOnlyList<Locale> enabled, Locale tenantDefault)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(enabled);

        bool Offered(Locale l) => enabled.Contains(l);

        if (Locale.TryResolve(context.Request.Query[QueryParameter].ToString(), out Locale asked) && Offered(asked))
        {
            return asked;
        }

        if (Locale.TryFromCode(context.User.FindFirstValue(BrightTimeClaims.Locale), out Locale own) && Offered(own))
        {
            return own;
        }

        if (context.Request.GetTypedHeaders().AcceptLanguage is { Count: > 0 } accepted)
        {
            foreach (var tag in accepted.Where(a => a.Quality is null or > 0).OrderByDescending(a => a.Quality ?? 1))
            {
                if (Locale.TryResolve(tag.Value.ToString(), out Locale browser) && Offered(browser))
                {
                    return browser;
                }
            }
        }

        return Offered(tenantDefault) ? tenantDefault : enabled.Count > 0 ? enabled[0] : Locale.Default;
    }
}
