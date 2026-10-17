using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Localisation;
using BrightTime.Web.Tenancy;
using Microsoft.AspNetCore.Antiforgery;

namespace BrightTime.Web.Finder;

/// <summary>
/// Saved listings without an account (decision D22): the ids live in a cookie on the device,
/// and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is stored about the person: no row, no id, no copy. The cookie holds public listing
/// ids and nothing more, is HttpOnly, and is sent only to this site. The cost is that saving
/// is per browser, and visible to anyone else who uses it (threat model T14), which the saved
/// page says, next to a button that clears it. When accounts arrive (M2), signing in can move
/// these into <c>opportunity.saved_item</c>.
/// </para>
/// <para>
/// Plain forms and redirects, so it works without JavaScript. Ids that are no longer published
/// are dropped when the list is shown, not trusted.
/// </para>
/// </remarks>
public static class SavedListings
{
    public const string CookieName = "brighttime.saved";
    /// <summary>The saved list's page.</summary>
    public const string PagePath = "/saved";

    /// <summary>Where the save, remove and clear forms post: not the page's own path, which Razor answers too.</summary>
    public const string ChangePath = "/saved/change";
    public const string CalendarPath = "/saved/calendar.ics";
    public const int MaxItems = 50;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    /// <summary>Most recently saved first.</summary>
    public static IReadOnlyList<Guid> Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? value = request.Cookies[CookieName];
        if (string.IsNullOrEmpty(value) || value.Length > MaxItems * 33)
        {
            return [];
        }

        List<Guid> ids = [];
        foreach (string part in value.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParseExact(part, "N", out Guid id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids.Count > MaxItems ? ids[..MaxItems] : ids;
    }

    public static bool Contains(HttpRequest request, Guid listing) => Read(request).Contains(listing);

    public static void Write(HttpResponse response, IReadOnlyList<Guid> ids, bool secure)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
            return;
        }

        response.Cookies.Append(CookieName, string.Join('.', ids.Take(MaxItems).Select(i => i.ToString("N", CultureInfo.InvariantCulture))), new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            MaxAge = Lifetime,
            Path = "/",
            IsEssential = true,
        });
    }

    /// <summary>A path on this site to come back to, or the saved page: never another host.</summary>
    public static string LocalPath(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Contains("://", StringComparison.Ordinal) && !url.Contains('\n', StringComparison.Ordinal) && !url.Contains('\r', StringComparison.Ordinal)
            ? url
            : PagePath;

    public static IEndpointRouteBuilder MapSavedListings(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(ChangePath, async (HttpContext http, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
            List<Guid> ids = [.. Read(http.Request)];
            string action = form["action"].ToString();
            bool known = Guid.TryParse(form["listing"].ToString(), out Guid listing);
            switch (action)
            {
                case "save" when known:
                    ids.Remove(listing);
                    ids.Insert(0, listing);
                    break;
                case "remove" when known:
                    ids.Remove(listing);
                    break;
                case "clear":
                    ids.Clear();
                    break;
                default:
                    return Results.BadRequest();
            }

            Write(http.Response, ids, http.Request.IsHttps);
            return Results.LocalRedirect(LocalPath(form["returnUrl"].ToString()));
        });

        endpoints.MapGet(CalendarPath, async (
            HttpContext http,
            IListingFinder finder,
            IListingDetails details,
            ITenantContext tenant,
            SiteContext site,
            Text text,
            IClock clock) =>
        {
            IReadOnlyList<Guid> ids = Read(http.Request);
            List<ListingDetail> listings = [];
            if (ids.Count > 0)
            {
                FinderPage page = await finder.SearchAsync(new SearchQuery
                {
                    TenantId = tenant.TenantId.Value,
                    ListingIds = ids,
                    PageSize = new PageSize(MaxItems),
                }, http.RequestAborted).ConfigureAwait(false);

                foreach (FinderItem item in page.Items)
                {
                    if (await details.GetPublishedAsync(tenant.TenantId, item.Slug, tenant.Locale.Code, http.RequestAborted).ConfigureAwait(false) is { } d)
                    {
                        listings.Add(d);
                    }
                }
            }

            Uri origin = site.BaseUrl ?? new Uri($"{http.Request.Scheme}://{http.Request.Host}/");
            ReminderWords words = new(
                text["saved.calendar.name"],
                (l, d) => text.Format("saved.calendar.deadline", ("title", l.Title),
                    ("deadline", d.Label ?? (text.Has("listing.deadline." + d.Kind) ? text["listing.deadline." + d.Kind] : text["listing.deadline.other"]))),
                text["saved.calendar.session_alarm"],
                text["saved.calendar.deadline_alarm"]);
            string ics = ReminderCalendar.ToIcs(listings, l => new Uri(origin, $"/listings/{Uri.EscapeDataString(l.Slug)}"), words, clock.UtcNow);

            // Personal: which listings someone saved. Not for any shared cache.
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), ListingCalendar.ContentType, "brighttime-saved.ics");
        });

        return endpoints;
    }
}
