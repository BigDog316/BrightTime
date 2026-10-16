using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Modules.Opportunities.Search.Sample;

/// <summary>
/// A small, fictional catalogue for Kettle County, NY, used to run the finder
/// before the real seed exists. Nothing here is a real provider, place or person.
/// </summary>
/// <remarks>
/// Schedules are generated with <see cref="OccurrenceExpander"/> relative to the
/// clock, so the sample always has upcoming sessions. A fixed date would go stale.
/// </remarks>
public static class KettleCatalogue
{
    public static readonly Guid TenantId = Guid.Parse("0196c3e8-0000-7000-8000-00000000f001");

    public const string Zone = "America/New_York";

    private static readonly Guid Org = Guid.Parse("0196c3e8-0000-7000-8000-00000000f010");
    private static readonly Guid OrgLibrary = Guid.Parse("0196c3e8-0000-7000-8000-00000000f011");
    private static readonly Guid OrgRec = Guid.Parse("0196c3e8-0000-7000-8000-00000000f012");

    private static readonly Guid SiteHigh = Guid.Parse("0196c3e8-0000-7000-8000-00000000f020");
    private static readonly Guid SiteLibrary = Guid.Parse("0196c3e8-0000-7000-8000-00000000f021");
    private static readonly Guid SiteRec = Guid.Parse("0196c3e8-0000-7000-8000-00000000f022");

    private static readonly Guid HoodWestside = Guid.Parse("0196c3e8-0000-7000-8000-00000000f030");
    private static readonly Guid HoodDowntown = Guid.Parse("0196c3e8-0000-7000-8000-00000000f031");
    private static readonly Guid HoodRiverside = Guid.Parse("0196c3e8-0000-7000-8000-00000000f032");

    /// <summary>The organisations in the sample, for tests that need a parent row.</summary>
    public static IReadOnlyList<Guid> Organisations { get; } = [Org, OrgLibrary, OrgRec];

    /// <summary>The sample's rows, projected as the real projector would produce them.</summary>
    public static IReadOnlyList<ListingSearchRow> Rows(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        DateOnly today = clock.TodayIn(TimeZoneInfo.FindSystemTimeZoneById(Zone));
        DateOnly from = today;
        DateOnly to = today.AddDays(70);

        List<ListingSearchRow> rows = [];
        foreach (ProjectionSource source in Sources(clock, from, to))
        {
            rows.AddRange(ListingSearchProjector.Project(source));
        }

        return rows;
    }

    private static IEnumerable<ProjectionSource> Sources(IClock clock, DateOnly from, DateOnly to)
    {
        SiteSource high = new(SiteHigh, HoodWestside, new Coordinate(41.0339, -73.7629));
        SiteSource library = new(SiteLibrary, HoodDowntown, new Coordinate(41.0287, -73.7701));
        SiteSource rec = new(SiteRec, HoodRiverside, new Coordinate(41.0412, -73.7512));

        yield return Build(clock, from, to,
            id: 1, org: Org, orgName: "Kettle Youth Collective",
            title: "Robotics club",
            summary: "Build and program robots with a team every week.",
            facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = Org,
                AgeMin = 11,
                AgeMax = 14,
                Categories = ["stem"],
                Interests = ["robotics", "engineering"],
            },
            sites: [high],
            weekly: (DayOfWeek.Friday, new TimeOnly(15, 30), new TimeOnly(17, 0)),
            costCents: 0, isFree: true,
            popularity: 62);

        yield return Build(clock, from, to,
            id: 2, org: OrgLibrary, orgName: "Kettle Public Library",
            title: "Teen coding lab",
            summary: "Learn to write your first app in a friendly, drop-in lab.",
            facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = OrgLibrary,
                AgeMin = 13,
                AgeMax = 17,
                Categories = ["stem"],
                Interests = ["coding"],
                Languages = ["en", "es"],
            },
            sites: [library],
            weekly: (DayOfWeek.Wednesday, new TimeOnly(16, 0), new TimeOnly(17, 30)),
            costCents: 0, isFree: true,
            popularity: 41);

        yield return Build(clock, from, to,
            id: 3, org: Org, orgName: "Kettle Youth Collective",
            title: "Youth orchestra",
            summary: "Rehearse and perform with other young musicians.",
            facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = Org,
                AgeMin = 8,
                AgeMax = 14,
                Categories = ["arts"],
                Interests = ["music"],
            },
            sites: [high],
            weekly: (DayOfWeek.Tuesday, new TimeOnly(17, 0), new TimeOnly(18, 30)),
            costCents: 12_000, isFree: false,
            popularity: 28);

        yield return Build(clock, from, to,
            id: 4, org: OrgRec, orgName: "Riverside Recreation",
            title: "Evening drop-in basketball",
            summary: "Open gym for pickup games. Just show up.",
            facets: new FacetInput
            {
                Kind = ListingKind.Resource,
                OrganisationId = OrgRec,
                AgeMin = 6,
                AgeMax = 17,
                Categories = ["sports"],
                Interests = ["basketball"],
                IsDropIn = true,
                Accommodations = ["wheelchair-accessible"],
            },
            sites: [rec],
            weekly: (DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(20, 0)),
            costCents: 0, isFree: true,
            popularity: 35);

        yield return Build(clock, from, to,
            id: 5, org: OrgRec, orgName: "Riverside Recreation",
            title: "Summer camp counsellor (paid)",
            summary: "Paid summer job helping run a day camp for younger children.",
            facets: new FacetInput
            {
                Kind = ListingKind.Job,
                OrganisationId = OrgRec,
                AgeMin = 16,
                AgeMax = 17,
                Categories = ["youth-work"],
                Interests = ["leadership"],
                CostModel = CostModel.StipendPaid,
            },
            sites: [rec],
            weekly: (DayOfWeek.Saturday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            // A stipended job is not free: is_free is generated from cost_model = 'free'.
            costCents: 0, isFree: false,
            popularity: 12);

        yield return Build(clock, from, to,
            id: 6, org: OrgLibrary, orgName: "Kettle Public Library",
            title: "Homework help online",
            summary: "Live online tutors for maths, reading and writing.",
            facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = OrgLibrary,
                AgeMin = 9,
                AgeMax = 14,
                Categories = ["academic"],
                Interests = ["reading", "maths"],
                Languages = ["en"],
                Modality = Modality.Virtual,
            },
            sites: [],
            weekly: (DayOfWeek.Thursday, new TimeOnly(17, 0), new TimeOnly(18, 0)),
            costCents: 0, isFree: true,
            popularity: 22);
    }

    private static ProjectionSource Build(
        IClock clock,
        DateOnly from,
        DateOnly to,
        int id,
        Guid org,
        string orgName,
        string title,
        string summary,
        FacetInput facets,
        SiteSource[] sites,
        (DayOfWeek Day, TimeOnly Start, TimeOnly End) weekly,
        int costCents,
        bool isFree,
        double popularity)
    {

        // Anchor the weekly rule on the first matching day inside the window, so
        // every sample session has at least one occurrence in the next ten weeks.
        DateOnly anchor = from;
        while (anchor.DayOfWeek != weekly.Day)
        {
            anchor = anchor.AddDays(1);
        }

        var rule = new SessionRule(
            Zone,
            anchor,
            to,
            weekly.Start,
            weekly.End,
            "FREQ=WEEKLY;BYDAY=" + ByDay(weekly.Day),
            []);

        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(rule, from, to);

        return new ProjectionSource
        {
            ListingId = SampleId(id),
            OrganisationId = org,
            OrganisationName = orgName,
            IsPublished = true,
            PublishedAt = clock.UtcNow.AddDays(-30 + id),
            Facets = facets,
            Title = title,
            Slug = SharedKernel.Primitives.Slug.From(title),
            Summary = summary,
            CategoryNames = facets.Categories.ToArray(),
            Sites = sites,
            OccurrenceStarts = [.. occurrences.Select(o => o.StartsAt)],
            OccurrenceEnds = [.. occurrences.Select(o => o.EndsAt)],
            Deadlines = [],
            Popularity = popularity,
            CostCents = costCents,
            IsFree = isFree,
        };
    }

    private static Guid SampleId(int id) =>
        Guid.Parse($"0196c3e8-0000-7000-8000-0000000f1{id:000}");

    private static string ByDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "SU",
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        _ => "SA",
    };
}
