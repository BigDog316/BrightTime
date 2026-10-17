namespace BrightTime.Seed;

/// <summary>An axis-aligned box in WGS84 degrees. The seed's geography is a grid of these.</summary>
internal readonly record struct Box(double West, double South, double East, double North)
{
    public (double Lon, double Lat) Centre => ((West + East) / 2, (South + North) / 2);

    /// <summary>A point inside the box, kept off the edges so it is unambiguously in one cell.</summary>
    public (double Lon, double Lat) PointAt(double fx, double fy) =>
        (West + ((East - West) * (0.1 + (0.8 * fx))), South + ((North - South) * (0.1 + (0.8 * fy))));
}

/// <summary>A row of <c>platform.neighbourhood</c>. A null box means "the union of my children".</summary>
internal sealed record SeedArea(Guid Id, Guid? ParentId, string Code, string Kind, string Name, Box? Box);

internal sealed record SeedDistrict(Guid Id, string Name, IReadOnlyList<Guid> MunicipalityIds);

internal sealed record SeedSchool(
    Guid Id,
    Guid DistrictId,
    string Name,
    short GradeMin,
    short GradeMax,
    double Lon,
    double Lat,
    Guid NeighbourhoodId);

internal sealed record SeedTransitStop(Guid Id, string GtfsId, string Name, double Lon, double Lat, bool WheelchairBoarding);

internal sealed record SeedTransit(Guid FeedId, string AgencyName, string FeedUrl, IReadOnlyList<SeedTransitStop> Stops);

/// <summary>A taxonomy term with its name in each locale that has one.</summary>
internal sealed record SeedTerm(
    Guid Id,
    string Vocabulary,
    string Code,
    Guid? ParentId,
    bool IsInterest,
    short SortOrder,
    IReadOnlyDictionary<string, string> Names);

internal sealed record SeedOrganisation(
    Guid Id,
    string DisplayName,
    string LegalName,
    string Slug,
    string Kind,
    string Status,
    string WebsiteUrl,
    string Mission,
    string PublicEmail,
    string PublicPhone,
    IReadOnlyList<string> LanguagesServed,
    IReadOnlyList<string> Focus,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt);

internal sealed record SeedSite(
    Guid Id,
    Guid OrgId,
    string Name,
    string AddressLine1,
    string Locality,
    string PostalCode,
    double Lon,
    double Lat,
    Guid NeighbourhoodId,
    bool IsAddressPublic,
    string? TransitNote,
    bool? IsWheelchairAccessible);

internal sealed record SeedSession(
    Guid Id,
    Guid? SiteId,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Rrule,
    IReadOnlyList<DateOnly> Exdates);

internal sealed record SeedDeadline(Guid Id, string Kind, DateTimeOffset DueAt, string? Label);

internal sealed record SeedTranslation(string Locale, string Title, string Summary, string? Description);

internal sealed record SeedListing
{
    public required Guid Id { get; init; }
    public required Guid OrgId { get; init; }
    public required string Kind { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public string? Description { get; init; }
    public required string PrimaryLanguage { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public DateTimeOffset? UnpublishedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public short? AgeMin { get; init; }
    public short? AgeMax { get; init; }
    public short? GradeMin { get; init; }
    public short? GradeMax { get; init; }
    public required string Modality { get; init; }
    public string? VirtualUrl { get; init; }
    public bool IsDropIn { get; init; }
    public bool MealsProvided { get; init; }
    public bool TransportProvided { get; init; }
    public string? TransportNote { get; init; }
    public required string CostModel { get; init; }
    public int? CostCentsMin { get; init; }
    public int? CostCentsMax { get; init; }
    public string? SlidingScaleNote { get; init; }
    public required string RegistrationMode { get; init; }
    public string? ExternalUrl { get; init; }
    public int? CapacityTotal { get; init; }
    public bool WaitlistEnabled { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }
    public string? AccessibilityNote { get; init; }
    public string? SeasonLabel { get; init; }
    public IReadOnlyList<Guid> SiteIds { get; init; } = [];
    public IReadOnlyList<Guid> TermIds { get; init; } = [];
    public IReadOnlyList<Guid> AccommodationTermIds { get; init; } = [];
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<SeedSession> Sessions { get; init; } = [];
    public IReadOnlyList<SeedDeadline> Deadlines { get; init; } = [];
    public IReadOnlyList<SeedTranslation> Translations { get; init; } = [];
}

internal sealed record SeedData(
    Guid TenantId,
    IReadOnlyList<SeedArea> Areas,
    IReadOnlyList<SeedDistrict> Districts,
    IReadOnlyList<SeedSchool> Schools,
    SeedTransit Transit,
    IReadOnlyList<SeedTerm> Terms,
    IReadOnlyList<SeedOrganisation> Organisations,
    IReadOnlyList<SeedSite> Sites,
    IReadOnlyList<SeedListing> Listings);
