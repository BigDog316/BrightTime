using System.Globalization;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Seed.Kettle;

/// <summary>
/// Sixty fictional providers and their 85 sites. Websites are on example.org and phone
/// numbers in 555-0100..0199, the ranges reserved for fiction, so nothing seeded can reach
/// a real person.
/// </summary>
internal static class KettleOrganisations
{
    private sealed record Spec(
        string Name,
        string Kind,
        string Home,
        string[] Focus,
        int Sites,
        string Status = "approved",
        string[]? Languages = null,
        bool PrivateSites = false,
        string[]? SiteTowns = null);

    /// <summary>The order is the identity: ids derive from names, phone numbers from position.</summary>
    private static readonly Spec[] Specs =
    [
        new("Kettle County Public Library", "library", "Kettleton", ["academic", "stem", "arts", "careers"], 6,
            Languages: ["en", "es", "zh-Hans"],
            SiteTowns: ["Kettleton", "Grayhaven", "Brannock", "Cedar Run", "Alder Crossing", "Quarry Hill"]),
        new("Kettleton Parks and Recreation", "public_agency", "Kettleton", ["sports", "outdoors", "arts", "careers"], 4, Languages: ["en", "es"]),
        new("Grayhaven Recreation Department", "public_agency", "Grayhaven", ["sports", "outdoors"], 2),
        new("Brannock Parks and Recreation", "public_agency", "Brannock", ["sports", "outdoors", "careers"], 2),
        new("Quarry Hill-Wexley Recreation Commission", "public_agency", "Quarry Hill", ["sports", "outdoors"], 2,
            SiteTowns: ["Quarry Hill", "Wexley"]),
        new("Kettleton City Schools Afterschool", "school", "Kettleton", ["academic", "stem", "arts"], 3, Languages: ["en", "es", "bn", "ar"]),
        new("Grayhaven-Brannock Extended Day", "school", "Grayhaven", ["academic", "sports"], 2, SiteTowns: ["Grayhaven", "Brannock"]),
        new("Alder Valley Community Schools", "school", "Alder Crossing", ["academic", "health"], 1),
        new("Quarry Hill-Wexley Afterschool", "school", "Quarry Hill", ["academic", "arts"], 1),
        new("Kettle Youth Collective", "nonprofit", "Kettleton", ["stem", "arts", "civic"], 3),
        new("Kettle Valley Family Center", "nonprofit", "Grayhaven", ["sports", "health", "life-skills"], 2,
            SiteTowns: ["Grayhaven", "Cedar Run"]),
        new("Riverside Youth Center", "nonprofit", "Kettleton", ["sports", "life-skills", "academic"], 1),
        new("Eastbank Youth Club", "nonprofit", "Kettleton", ["sports", "academic", "arts"], 2),
        new("Westside Arts Studio", "nonprofit", "Kettleton", ["arts"], 1),
        new("Northgate Music School", "nonprofit", "Kettleton", ["arts"], 1),
        new("Kettle County Youth Orchestra", "nonprofit", "Kettleton", ["arts"], 1),
        new("Grayhaven Dance Collective", "nonprofit", "Grayhaven", ["arts", "sports"], 1),
        new("Mill District Makerspace", "nonprofit", "Brannock", ["stem", "careers"], 1),
        new("Kettle Code Club", "informal_group", "Kettleton", ["stem"], 1, PrivateSites: true),
        new("Cedar Run Nature Center", "nonprofit", "Cedar Run", ["outdoors", "stem"], 1),
        new("Lakeview Paddling Club", "nonprofit", "Cedar Run", ["sports", "outdoors"], 1),
        new("Kettle County Youth Conservation Corps", "public_agency", "Fenwick Hollow", ["outdoors", "careers"], 2,
            SiteTowns: ["Fenwick Hollow", "Hollis Bend"]),
        new("Hollis Bend Community Garden", "informal_group", "Hollis Bend", ["outdoors", "life-skills"], 1),
        new("Marrow Point Soccer League", "nonprofit", "Marrow Point", ["sports"], 1),
        new("Kettle County Swim Club", "nonprofit", "Kettleton", ["sports", "careers"], 2, SiteTowns: ["Kettleton", "Brannock"]),
        new("Orrin Valley Martial Arts Academy", "business", "Orrin Valley", ["sports"], 1),
        new("Kettleton Youth Basketball Association", "nonprofit", "Kettleton", ["sports"], 2),
        new("Wexley Track Club", "informal_group", "Wexley", ["sports"], 1),
        new("Kettle County Community College Youth Programs", "higher_ed", "Kettleton", ["college-prep", "stem", "careers"], 2),
        new("Grayhaven College Access Network", "nonprofit", "Grayhaven", ["college-prep"], 1),
        new("First Generation Scholars of Kettle County", "nonprofit", "Brannock", ["college-prep", "academic"], 1),
        new("Kettle County Workforce Board", "public_agency", "Kettleton", ["careers"], 1),
        new("Kettle Summer Jobs Partnership", "nonprofit", "Kettleton", ["careers"], 2, SiteTowns: ["Kettleton", "Orrin Valley"]),
        new("Brannock Chamber Youth Internships", "nonprofit", "Brannock", ["careers", "civic"], 1),
        new("Kettle Teen Health Collaborative", "nonprofit", "Kettleton", ["health"], 2, SiteTowns: ["Kettleton", "Marrow Point"]),
        new("Mindful Youth Kettle County", "nonprofit", "Grayhaven", ["health"], 1),
        new("Kettle County Youth Council", "public_agency", "Kettleton", ["civic"], 1),
        new("Kettleton Debate League", "nonprofit", "Kettleton", ["civic", "academic"], 1),
        new("Volunteer Kettle", "nonprofit", "Kettleton", ["civic"], 1),
        new("Casa de la Comunidad Kettleton", "nonprofit", "Kettleton", ["academic", "life-skills", "arts"], 2, Languages: ["es", "en"]),
        new("Kettle Chinese Cultural Association", "nonprofit", "Grayhaven", ["arts", "academic"], 1, Languages: ["zh-Hans", "en"]),
        new("Bangladeshi American Youth Alliance", "nonprofit", "Brannock", ["academic", "civic"], 1, Languages: ["bn", "en"]),
        new("Lakou Ayisyen Youth Program", "nonprofit", "Cedar Run", ["arts", "academic"], 1, Languages: ["ht", "en"]),
        new("Arab American Family Center", "nonprofit", "Kettleton", ["academic", "life-skills"], 1, Languages: ["ar", "en"]),
        new("St. Brendan Parish Youth Ministry", "faith_based", "Grayhaven", ["civic", "sports"], 1),
        new("Kettle Interfaith Youth Network", "faith_based", "Kettleton", ["civic"], 1),
        new("Brannock Islamic Center Youth Circle", "faith_based", "Brannock", ["academic", "civic"], 1, Languages: ["en", "ar", "bn"]),
        new("Kettle Kitchen Collective", "nonprofit", "Orrin Valley", ["life-skills", "careers"], 1),
        new("Money Smart Teens", "nonprofit", "Kettleton", ["life-skills"], 1),
        new("Quarry Hill Historical Society", "nonprofit", "Quarry Hill", ["academic", "civic"], 1),
        new("Kettle County Theater Company", "nonprofit", "Kettleton", ["arts"], 1),
        new("Fenwick Hollow Photography Club", "informal_group", "Fenwick Hollow", ["arts"], 1, PrivateSites: true),
        new("Alder Crossing Robotics Boosters", "informal_group", "Alder Crossing", ["stem"], 1, PrivateSites: true),
        new("Wexley Learning Studio", "business", "Wexley", ["academic"], 1, PrivateSites: true),

        // Not yet approved: they exist so the organiser review queue has something to review.
        new("Northgate Film Society", "nonprofit", "Kettleton", ["arts"], 1, Status: "applied"),
        new("Kettle Skate Collective", "informal_group", "Brannock", ["sports"], 1, Status: "applied"),
        new("Eastbank Esports League", "nonprofit", "Kettleton", ["stem"], 1, Status: "applied"),
        new("Grayhaven Young Builders", "nonprofit", "Grayhaven", ["stem"], 1, Status: "under_review"),
        new("Wexley Chess Club", "informal_group", "Wexley", ["academic"], 1, Status: "under_review", PrivateSites: true),
        new("Hollis Bend Teen Farm", "nonprofit", "Hollis Bend", ["outdoors", "careers"], 1, Status: "changes_requested"),
    ];

    private static readonly string[] Streets =
    [
        "Orchard Hill Road", "Market Square", "River Road", "Linden Avenue", "Mill Street", "Station Plaza",
        "Cedar Lane", "Union Street", "Grove Avenue", "Church Street", "Lake Shore Drive", "Quarry Road",
        "Maple Avenue", "Hawthorne Street", "Commons Way", "Ferry Street", "Highland Avenue", "Brook Street",
        "Valley Road", "Elm Place",
    ];

    public static IReadOnlyList<SeedOrganisation> Organisations(DateTimeOffset now)
    {
        List<SeedOrganisation> result = [];

        for (int i = 0; i < Specs.Length; i++)
        {
            Spec s = Specs[i];
            string slug = Slug.From(s.Name);
            DateTimeOffset submitted = now.AddDays(-400 + (i * 3));

            // ck_organisation_approved: approved_at exists exactly when the status is approved.
            DateTimeOffset? approved = s.Status == "approved" ? submitted.AddDays(9) : null;
            DateTimeOffset? submittedAt = s.Status == "approved" ? submitted : now.AddDays(-(i % 7) - 1);

            result.Add(new SeedOrganisation(
                OrgId(s.Name),
                s.Name,
                LegalName(s),
                slug,
                s.Kind,
                s.Status,
                $"https://example.org/kettle/{slug}",
                Mission(s),
                $"hello@{slug}.example.org",
                $"(555) 555-01{i % 100:00}",
                s.Languages ?? ["en"],
                s.Focus,
                submittedAt,
                approved));
        }

        return result;
    }

    public static Guid OrgId(string name) => SeedIds.For("org/" + name);

    public static IReadOnlyList<SeedSite> Sites(IReadOnlyList<SeedArea> areas)
    {
        List<SeedSite> sites = [];

        foreach (Spec s in Specs)
        {
            Guid org = OrgId(s.Name);

            for (int n = 0; n < s.Sites; n++)
            {
                string town = s.SiteTowns is { } towns ? towns[n % towns.Length] : s.Home;
                IReadOnlyList<SeedArea> hoods = KettleGeography.NeighbourhoodsOf(areas, town);
                SeedArea hood = hoods[(SeedIds.Hash($"{s.Name}/{n}") + n) % hoods.Count];

                string key = $"site/{s.Name}/{n}";
                (double lon, double lat) = hood.Box!.Value.PointAt(
                    KettleGeography.Fraction(key + "/x"), KettleGeography.Fraction(key + "/y"));

                int number = 10 + (SeedIds.Hash(key + "/no") % 980);
                string street = Streets[SeedIds.Hash(key + "/street") % Streets.Length];
                int route = KettleGeography.RouteFor(areas, hood.Id);

                // A private site's address is real data the provider holds; the public never sees it.
                string? transit = SeedIds.Hash(key + "/transit") % 2 == 0
                    ? $"Kettle County Transit route {route} stops on {street}."
                    : null;

                bool? accessible = (SeedIds.Hash(key + "/access") % 10) switch
                {
                    < 7 => true,
                    7 => false,
                    _ => null,
                };

                sites.Add(new SeedSite(
                    SeedIds.For(key),
                    org,
                    SiteName(s, n, hood, town),
                    $"{number.ToString(CultureInfo.InvariantCulture)} {street}",
                    town,
                    KettleGeography.PostalCodeFor(town),
                    lon,
                    lat,
                    hood.Id,
                    !s.PrivateSites,
                    transit,
                    accessible));
            }
        }

        return sites;
    }

    private static string SiteName(Spec s, int n, SeedArea hood, string town) => s.Kind switch
    {
        "library" => n == 0 ? "Main Library" : $"{town} Branch Library",
        "public_agency" when s.Focus.Contains("sports") => n % 2 == 0 ? $"{hood.Name} Community Center" : $"{hood.Name} Park Fieldhouse",
        "school" => $"{hood.Name} {(n % 2 == 0 ? "Elementary School" : "Middle School")}",
        _ when s.PrivateSites => n == 0 ? "Club meeting space" : $"Meeting space {n + 1}",
        _ => s.Sites == 1 ? s.Name : $"{s.Name}, {hood.Name}",
    };

    private static string LegalName(Spec s) => s.Kind switch
    {
        "nonprofit" => s.Name + " Inc.",
        "business" => s.Name + " LLC",
        _ => s.Name,
    };

    private static string Mission(Spec s)
    {
        string what = string.Join(", ", s.Focus.Select(f => f.Replace('-', ' ')));
        return $"{s.Name} offers young people in Kettle County programs in {what}.";
    }
}
