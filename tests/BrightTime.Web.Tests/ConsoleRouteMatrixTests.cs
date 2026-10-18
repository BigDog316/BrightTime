using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Every console page, found from the application's own endpoints rather than listed here: an
/// organisation's pages refuse another organisation's owner, the organiser's pages refuse
/// providers, and each page opens for the person it is for, so a refusal is never the test's
/// own bad id. A page added later is covered without being named, and a route parameter this
/// test cannot fill fails it until it can.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed partial class ConsoleRouteMatrixTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    // Read-only visits: other tests may use these organisations too.
    private SeedOrganisation Victim => db.Data.Organisations.Single(o => o.Slug == "kettle-youth-collective");

    private SeedOrganisation Intruder => db.Data.Organisations.Single(o => o.Slug == "riverside-youth-center");

    private static string Admin(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    [Fact]
    public async Task An_organisations_pages_refuse_another_organisations_owner_and_open_for_its_own()
    {
        RequireDatabase();
        string[] pages = [.. Routes().Where(r => r.StartsWith("/provider/orgs/", StringComparison.Ordinal))];
        pages.Length.ShouldBeGreaterThan(8, "the provider console's organisation pages");

        using Browser intruder = db.NewBrowser();
        await intruder.SignInAsync(Admin(Intruder));
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(Victim));

        List<string> problems = [];
        foreach (string route in pages)
        {
            string path = Fill(route);
            HttpStatusCode refused = (await intruder.GetAsync(path)).StatusCode;
            if (refused is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
            {
                problems.Add($"{route}: another organisation's owner got {(int)refused}");
            }

            HttpStatusCode opened = (await owner.GetAsync(path)).StatusCode;
            if (opened != HttpStatusCode.OK)
            {
                problems.Add($"{route}: its own owner got {(int)opened}");
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_organisers_pages_refuse_providers_and_open_for_organisers()
    {
        RequireDatabase();
        string[] pages = [.. Routes().Where(r => r.StartsWith("/organiser", StringComparison.Ordinal))];
        pages.Length.ShouldBeGreaterThan(5, "the organiser console");

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(Intruder));
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);

        List<string> problems = [];
        foreach (string route in pages)
        {
            string path = Fill(route);
            HttpResponseMessage refused = await provider.GetAsync(path);
            bool deniedRedirect = refused.StatusCode == HttpStatusCode.Redirect
                                  && refused.Headers.Location!.ToString().Contains("/access-denied", StringComparison.Ordinal);
            if (refused.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound) && !deniedRedirect)
            {
                problems.Add($"{route}: a provider got {(int)refused.StatusCode}");
            }

            HttpStatusCode opened = (await organiser.GetAsync(path)).StatusCode;
            if (opened != HttpStatusCode.OK)
            {
                problems.Add($"{route}: the organiser got {(int)opened}");
            }
        }

        problems.ShouldBeEmpty();
    }

    /// <summary>The console's GET routes, from the application's endpoint table.</summary>
    private IEnumerable<string> Routes() =>
        db.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(r => r.StartsWith("/provider", StringComparison.Ordinal) || r.StartsWith("/organiser", StringComparison.Ordinal))
            .Where(r => !r.EndsWith(".csv", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>A route with its parameters filled with the victim organisation's ids.</summary>
    private string Fill(string route)
    {
        Guid listing = db.Data.Listings.First(l => l.OrgId == Victim.Id && l.Status == "published").Id;
        Guid site = db.Data.Sites.First(s => s.OrgId == Victim.Id).Id;
        return Parameter().Replace(route, m => m.Groups[1].Value switch
        {
            "OrgId" => Victim.Id.ToString(),
            "ListingId" => listing.ToString(),
            "SiteId" => site.ToString(),
            "Vocabulary" => "category",
            "TermId" => KettleTaxonomy.TermId("category", "stem").ToString(),
            string other => throw new InvalidOperationException($"{route}: the matrix cannot fill {{{other}}}; add a value for it."),
        });
    }

    [GeneratedRegex(@"\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex Parameter();
}
