using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.E2E.Tests;

/// <summary>
/// Every public route the accessibility and keyboard gates visit, including the empty, filtered
/// and missing states. A new public page belongs here, or neither gate sees it.
/// </summary>
/// <remarks>
/// Routes that need seeded data are named by key and resolved at run time, so the data stays in
/// the seed. Console pages are behind sign-in; the keyboard gate covers their flows instead.
/// </remarks>
public static class PublicRoutes
{
    public static readonly string[] All =
    [
        "/",
        "/search",
        "/search?q=soccer&free=1&sort=soonest",
        "/search?q=zzzz-nothing-matches",
        "/search?view=map",
        "{listing}",
        "/collections",
        "/collections/" + KettleCuration.FreeCollection,
        "/neighbourhoods",
        "/neighbourhoods/hood-downtown",
        "{organisation}",
        "/saved",
        "/providers/apply",
        "/sign-in",
        "/account/forgot",
        "/account/reset",
        "/account/claim",
        "/app/guardian-links/00000000-0000-0000-0000-000000000001/verify",
        "/access-denied",
        "/listings/no-such-listing",
    ];

    /// <summary>The routes on each engine, for theory data.</summary>
    public static TheoryData<string, string> OnEachEngine(params string[] engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        TheoryData<string, string> data = [];
        foreach (string engine in engines)
        {
            foreach (string route in All)
            {
                data.Add(engine, route);
            }
        }

        return data;
    }

    public static string Resolve(SiteFixture site, string route)
    {
        ArgumentNullException.ThrowIfNull(site);
        return route switch
        {
            "{listing}" => "/listings/" + PublishedListing(site).Slug,
            "{organisation}" => "/organisations/" + Organisation(site, PublishedListing(site).OrgId).Slug,
            _ => route,
        };
    }

    /// <summary>A published listing of an approved organisation: the first by slug, so runs agree.</summary>
    internal static SeedListing PublishedListing(SiteFixture site) => site.Data.Listings
        .Where(l => l.Status == "published" && Organisation(site, l.OrgId).Status == "approved")
        .OrderBy(l => l.Slug, StringComparer.Ordinal)
        .First();

    internal static SeedOrganisation Organisation(SiteFixture site, Guid id) => site.Data.Organisations.Single(o => o.Id == id);
}
