using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
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
    private static readonly Guid SiteStudio = Guid.Parse("0196c3e8-0000-7000-8000-00000000f023");

    private static readonly Guid HoodWestside = Guid.Parse("0196c3e8-0000-7000-8000-00000000f030");
    private static readonly Guid HoodDowntown = Guid.Parse("0196c3e8-0000-7000-8000-00000000f031");
    private static readonly Guid HoodRiverside = Guid.Parse("0196c3e8-0000-7000-8000-00000000f032");

    /// <summary>The organisations in the sample, for tests that need a parent row.</summary>
    public static IReadOnlyList<Guid> Organisations { get; } = [Org, OrgLibrary, OrgRec];

    /// <summary>How far ahead the sample's schedules run.</summary>
    private const int WindowDays = 70;

    /// <summary>The sample's rows, projected as the real projector would produce them.</summary>
    public static IReadOnlyList<ListingSearchRow> Rows(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        List<ListingSearchRow> rows = [];
        foreach (SampleListing listing in Listings())
        {
            rows.AddRange(ListingSearchProjector.Project(ToSource(clock, listing)));
        }

        return rows;
    }

    /// <summary>
    /// The sample's listing pages. Every session in the window is included; the reader picks
    /// the upcoming ones at request time, so a long-running dev server does not go stale.
    /// </summary>
    public static IReadOnlyList<ListingDetail> Details(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return [.. Listings().Select(l => ToDetail(clock, l))];
    }

    private static IEnumerable<SampleListing> Listings()
    {
        SampleSite high = new(SiteHigh, "Westside High School, room 114", HoodWestside, "Westside",
            new Coordinate(41.0339, -73.7629), IsAddressPublic: true,
            "40 Orchard Hill Road", "Kettleton, NY", "Bus 7 to Orchard Hill; the entrance is on the north side.", true);
        SampleSite library = new(SiteLibrary, "Kettle Public Library, Main Branch", HoodDowntown, "Downtown",
            new Coordinate(41.0287, -73.7701), IsAddressPublic: true,
            "2 Market Square", "Kettleton, NY", "Two blocks from the Market Square train station.", true);
        SampleSite rec = new(SiteRec, "Riverside Recreation Center", HoodRiverside, "Riverside",
            new Coordinate(41.0412, -73.7512), IsAddressPublic: true,
            "315 River Road", "Kettleton, NY", null, true);

        // A private site: the rehearsal studio is in a home, so families see only the
        // neighbourhood until they register.
        SampleSite studio = new(SiteStudio, "Rehearsal studio", HoodWestside, "Westside",
            new Coordinate(41.0351, -73.7655), IsAddressPublic: false,
            "18 Linden Lane", "Kettleton, NY", null, null);

        yield return new SampleListing(
            Id: 1, Org: Org, OrgName: "Kettle Youth Collective",
            Title: "Robotics club",
            Summary: "Build and program robots with a team every week.",
            Description: "Teams of four design, build and program a robot over the season, then compete at the county showcase in May.\n\nNo experience needed. Laptops and kits are provided.",
            Facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = Org,
                AgeMin = 11,
                AgeMax = 14,
                Categories = ["stem"],
                Interests = ["robotics", "engineering"],
                MealsProvided = true,
            },
            Sites: [high],
            Weekly: (DayOfWeek.Friday, new TimeOnly(15, 30), new TimeOnly(17, 0)),
            CostCents: 0, IsFree: true,
            Popularity: 62,
            Registration: RegistrationMode.External,
            ExternalUrl: "https://example.org/kettle-youth/robotics");

        yield return new SampleListing(
            Id: 2, Org: OrgLibrary, OrgName: "Kettle Public Library",
            Title: "Teen coding lab",
            Summary: "Learn to write your first app in a friendly, drop-in lab.",
            Description: "Bring a project or start one. Library staff and volunteer mentors help with Python, web pages and game making.",
            Facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = OrgLibrary,
                AgeMin = 13,
                AgeMax = 17,
                Categories = ["stem"],
                Interests = ["coding"],
                Languages = ["en", "es"],
                IsDropIn = true,
            },
            Sites: [library],
            Weekly: (DayOfWeek.Wednesday, new TimeOnly(16, 0), new TimeOnly(17, 30)),
            CostCents: 0, IsFree: true,
            Popularity: 41,
            Registration: RegistrationMode.None);

        yield return new SampleListing(
            Id: 3, Org: Org, OrgName: "Kettle Youth Collective",
            Title: "Youth orchestra",
            Summary: "Rehearse and perform with other young musicians.",
            Description: "Weekly rehearsals and two concerts a year. Instruments can be borrowed, and reduced fees are available on request.",
            Facets: new FacetInput
            {
                Kind = ListingKind.Program,
                OrganisationId = Org,
                AgeMin = 8,
                AgeMax = 14,
                Categories = ["arts"],
                Interests = ["music"],
                CostModel = CostModel.Fixed,
            },
            Sites: [studio],
            Weekly: (DayOfWeek.Tuesday, new TimeOnly(17, 0), new TimeOnly(18, 30)),
            CostCents: 12_000, IsFree: false,
            Popularity: 28,
            Registration: RegistrationMode.Builtin,
            Deadline: ("registration", 14, "Registration closes"));

        yield return new SampleListing(
            Id: 4, Org: OrgRec, OrgName: "Riverside Recreation",
            Title: "Evening drop-in basketball",
            Summary: "Open gym for pickup games. Just show up.",
            Description: null,
            Facets: new FacetInput
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
            Sites: [rec],
            Weekly: (DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(20, 0)),
            CostCents: 0, IsFree: true,
            Popularity: 35,
            Registration: RegistrationMode.None,
            AccessibilityNote: "Step-free entrance and an accessible changing room.");

        yield return new SampleListing(
            Id: 5, Org: OrgRec, OrgName: "Riverside Recreation",
            Title: "Summer camp counsellor (paid)",
            Summary: "Paid summer job helping run a day camp for younger children.",
            Description: "Lead games, crafts and swim time for campers aged 6 to 10. Training is paid and happens in June.",
            Facets: new FacetInput
            {
                Kind = ListingKind.Job,
                OrganisationId = OrgRec,
                AgeMin = 16,
                AgeMax = 17,
                Categories = ["youth-work"],
                Interests = ["leadership"],
                CostModel = CostModel.StipendPaid,
            },
            Sites: [rec],
            Weekly: (DayOfWeek.Saturday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            // A stipended job is not free: is_free is generated from cost_model = 'free'.
            CostCents: 0, IsFree: false,
            Popularity: 12,
            Registration: RegistrationMode.External,
            ExternalUrl: "https://example.org/riverside-rec/jobs/counsellor",
            Deadline: ("application", 21, "Applications due"));

        yield return new SampleListing(
            Id: 6, Org: OrgLibrary, OrgName: "Kettle Public Library",
            Title: "Homework help online",
            Summary: "Live online tutors for maths, reading and writing.",
            Description: "Join a video session with a trained tutor. A library card is needed to sign in.",
            Facets: new FacetInput
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
            Sites: [],
            Weekly: (DayOfWeek.Thursday, new TimeOnly(17, 0), new TimeOnly(18, 0)),
            CostCents: 0, IsFree: true,
            Popularity: 22,
            Registration: RegistrationMode.External,
            ExternalUrl: "https://example.org/kettle-library/homework-help");
    }

    private static IReadOnlyList<Occurrence> Schedule(IClock clock, SampleListing listing)
    {
        DateOnly from = clock.TodayIn(TimeZoneInfo.FindSystemTimeZoneById(Zone));
        DateOnly to = from.AddDays(WindowDays);

        // Anchor the weekly rule on the first matching day inside the window, so
        // every sample session has at least one occurrence in the next ten weeks.
        DateOnly anchor = from;
        while (anchor.DayOfWeek != listing.Weekly.Day)
        {
            anchor = anchor.AddDays(1);
        }

        var rule = new SessionRule(
            Zone,
            anchor,
            to,
            listing.Weekly.Start,
            listing.Weekly.End,
            "FREQ=WEEKLY;BYDAY=" + ByDay(listing.Weekly.Day),
            []);

        return OccurrenceExpander.Expand(rule, from, to);
    }

    private static ProjectionSource ToSource(IClock clock, SampleListing listing)
    {
        IReadOnlyList<Occurrence> occurrences = Schedule(clock, listing);

        return new ProjectionSource
        {
            ListingId = SampleId(listing.Id),
            OrganisationId = listing.Org,
            OrganisationName = listing.OrgName,
            IsPublished = true,
            PublishedAt = clock.UtcNow.AddDays(-30 + listing.Id),
            Facets = listing.Facets,
            Title = listing.Title,
            Slug = Slug.From(listing.Title),
            Summary = listing.Summary,
            CategoryNames = listing.Facets.Categories.ToArray(),
            Sites = [.. listing.Sites.Select(s => new SiteSource(s.Id, s.HoodId, s.Location))],
            OccurrenceStarts = [.. occurrences.Select(o => o.StartsAt)],
            OccurrenceEnds = [.. occurrences.Select(o => o.EndsAt)],
            Deadlines = DeadlineOf(clock, listing) is { } d ? [d.DueAt] : [],
            Popularity = listing.Popularity,
            CostCents = listing.CostCents,
            IsFree = listing.IsFree,
        };
    }

    private static ListingDetail ToDetail(IClock clock, SampleListing listing)
    {
        FacetInput f = listing.Facets;

        return new ListingDetail
        {
            Id = new ListingId(SampleId(listing.Id)),
            Slug = Slug.From(listing.Title),
            Kind = f.Kind,
            Title = listing.Title,
            Summary = listing.Summary,
            Description = listing.Description,
            OrganisationName = listing.OrgName,
            AgeMin = f.AgeMin,
            AgeMax = f.AgeMax,
            GradeMin = f.GradeMin,
            GradeMax = f.GradeMax,
            CostModel = f.CostModel,
            CostCentsMin = f.CostModel == CostModel.Free ? null : listing.CostCents,
            Modality = f.Modality,
            RegistrationMode = listing.Registration,
            ExternalUrl = listing.ExternalUrl,
            MealsProvided = f.MealsProvided,
            TransportProvided = f.TransportProvided,
            IsDropIn = f.IsDropIn,
            AccessibilityNote = listing.AccessibilityNote,
            Categories = [.. f.Categories],
            Languages = [.. f.Languages],
            Accommodations = [.. f.Accommodations],
            Places = [.. listing.Sites.Select(s => new ListingPlace(
                s.Name,
                s.IsAddressPublic,
                s.IsAddressPublic ? s.AddressLine1 : null,
                null,
                s.IsAddressPublic ? s.Locality : null,
                null,
                s.HoodName,
                s.IsAddressPublic ? s.TransitNote : null,
                s.IsWheelchairAccessible,
                s.HoodId))],
            Upcoming = [.. Schedule(clock, listing).Select(o => new ListingTime(o.StartsAt, o.EndsAt))],
            Deadlines = DeadlineOf(clock, listing) is { } d ? [d] : [],
        };
    }

    /// <summary>Due at 11:59 pm local time, the day the sample says, so it reads as a real deadline.</summary>
    private static ListingDeadlineView? DeadlineOf(IClock clock, SampleListing listing)
    {
        if (listing.Deadline is not { } d)
        {
            return null;
        }

        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        DateTime local = clock.TodayIn(zone).AddDays(d.InDays).ToDateTime(new TimeOnly(23, 59));
        DateTimeOffset due = new(local, zone.GetUtcOffset(local));
        return new ListingDeadlineView(d.Kind, due.ToUniversalTime(), d.Label);
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

    /// <summary>
    /// A site as Directory holds it. The address is fictional; a private one exists here
    /// only so the sample can prove it is never shown.
    /// </summary>
    private sealed record SampleSite(
        Guid Id,
        string Name,
        Guid HoodId,
        string HoodName,
        Coordinate Location,
        bool IsAddressPublic,
        string AddressLine1,
        string Locality,
        string? TransitNote,
        bool? IsWheelchairAccessible);

    private sealed record SampleListing(
        int Id,
        Guid Org,
        string OrgName,
        string Title,
        string Summary,
        string? Description,
        FacetInput Facets,
        SampleSite[] Sites,
        (DayOfWeek Day, TimeOnly Start, TimeOnly End) Weekly,
        int CostCents,
        bool IsFree,
        double Popularity,
        RegistrationMode Registration,
        string? ExternalUrl = null,
        string? AccessibilityNote = null,
        (string Kind, int InDays, string Label)? Deadline = null);
}
