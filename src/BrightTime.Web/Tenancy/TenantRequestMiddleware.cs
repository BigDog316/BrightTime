using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Composition;
using Microsoft.Extensions.Options;

namespace BrightTime.Web.Tenancy;

/// <summary>
/// Sets the tenant and locale for the request, before any page reads them.
/// </summary>
/// <remarks>
/// The locale comes from <c>?lang=</c> when it names a supported locale, otherwise from
/// <c>Accept-Language</c>, otherwise the tenant default. A visitor can therefore always
/// read the finder in their own language, and the URL can say which one was chosen.
/// </remarks>
public sealed class TenantRequestMiddleware(RequestDelegate next, IOptions<FinderOptions> options)
{
    public const string LanguageParameter = "lang";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);

        FinderOptions finder = options.Value;

        Guid tenantId = finder.Mode == FinderMode.Sample ? KettleCatalogue.TenantId : finder.TenantId;
        TimeZoneInfo zone = ResolveZone(finder.TimeZone);
        Locale locale = ResolveLocale(context);

        tenant.SetTenant(new SharedKernel.Ids.TenantId(tenantId), zone, locale);

        await next(context).ConfigureAwait(false);
    }

    internal static Locale ResolveLocale(HttpContext context)
    {
        string? requested = context.Request.Query[LanguageParameter].ToString();
        if (Locale.TryFromCode(requested, out Locale fromQuery) && !string.IsNullOrEmpty(requested))
        {
            return fromQuery;
        }

        string? acceptLanguage = context.Request.Headers.AcceptLanguage.ToString();
        string? first = acceptLanguage?.Split(',')[0].Split(';')[0].Trim();
        return Locale.Resolve(first);
    }

    private static TimeZoneInfo ResolveZone(string id)
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
