using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Tenancy;
using Microsoft.Net.Http.Headers;

namespace BrightTime.Web.Localisation;

/// <summary>
/// Which language a request is answered in, among the tenant's enabled locales.
/// </summary>
/// <remarks>
/// In order: <c>?lang=</c> (a language link, which is also remembered); the remembered choice;
/// the signed-in account's own language; the browser's <c>Accept-Language</c>, best quality
/// first; the tenant's default. A locale the tenant has not enabled is passed over at every
/// step, so a link to <c>?lang=ar</c> on a tenant without Arabic gets its default instead.
/// </remarks>
public static class LocaleSelection
{
    public const string QueryParameter = "lang";
    public const string CookieName = "brighttime.lang";

    public static (Locale Locale, bool Chosen) Resolve(
        HttpContext context,
        IReadOnlyList<Locale> enabled,
        Locale tenantDefault,
        string? accountLocale)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(enabled);

        bool Offered(Locale l) => enabled.Contains(l);

        string? asked = context.Request.Query[QueryParameter].ToString();
        if (Locale.TryFromCode(asked, out Locale fromQuery) && Offered(fromQuery))
        {
            return (fromQuery, true);
        }

        if (Locale.TryFromCode(context.Request.Cookies[CookieName], out Locale remembered) && Offered(remembered))
        {
            return (remembered, false);
        }

        if (Locale.TryFromCode(accountLocale, out Locale own) && Offered(own))
        {
            return (own, false);
        }

        if (context.Request.GetTypedHeaders().AcceptLanguage is { Count: > 0 } accepted)
        {
            foreach (StringWithQualityHeaderValue tag in accepted
                         .Where(a => a.Quality is null or > 0)
                         .OrderByDescending(a => a.Quality ?? 1))
            {
                if (Locale.TryResolve(tag.Value.ToString(), out Locale browser) && Offered(browser))
                {
                    return (browser, false);
                }
            }
        }

        return (Offered(tenantDefault) ? tenantDefault : enabled.Count > 0 ? enabled[0] : Locale.Default, false);
    }
}

/// <summary>
/// Settles the request's locale once the account is known, remembers an explicit choice, and
/// loads the tenant's wording for the page.
/// </summary>
public sealed class LocaleMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan Remembered = TimeSpan.FromDays(365);

    public async Task InvokeAsync(HttpContext context, TenantContext tenant, SiteContext site, Text text)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(text);

        if (tenant.IsResolved)
        {
            IReadOnlyList<Locale> enabled = site.Site?.EnabledLocales ?? Locale.Supported;
            Locale fallback = site.Site is { } s && Locale.TryFromCode(s.DefaultLocale, out Locale d) ? d : Locale.Default;
            (Locale locale, bool chosen) = LocaleSelection.Resolve(
                context, enabled, fallback, context.User.FindFirst(BrightTimeClaims.Locale)?.Value);

            tenant.SetLocale(locale);
            if (chosen && context.Request.Cookies[LocaleSelection.CookieName] != locale.Code)
            {
                // A preference, not a tracker: the language code and nothing else.
                context.Response.Cookies.Append(LocaleSelection.CookieName, locale.Code, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                    MaxAge = Remembered,
                    Path = "/",
                    IsEssential = true,
                });
            }

            // The same address answers in different languages.
            context.Response.Headers.Append(HeaderNames.Vary, "Accept-Language, Cookie");
            await text.LoadAsync(context.RequestAborted).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);
    }
}
