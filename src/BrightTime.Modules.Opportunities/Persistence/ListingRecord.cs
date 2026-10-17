using NpgsqlTypes;

namespace BrightTime.Modules.Opportunities.Persistence;

/// <summary>
/// A row of <c>opportunity.listing</c>, mapped column for column. The drift test fails
/// if this and the DDL disagree in either direction.
/// </summary>
/// <remarks>
/// <see cref="AgeRange"/>, <see cref="GradeRange"/> and <see cref="IsFree"/> are generated
/// by the database and are never written from here.
/// </remarks>
public sealed class ListingRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public required string Kind { get; set; }

    public required string Slug { get; set; }

    public required string Title { get; set; }

    public required string Summary { get; set; }

    public string? Description { get; set; }

    public string PrimaryLanguage { get; set; } = "en";

    public string Status { get; set; } = "draft";

    public short? AgeMin { get; set; }

    public short? AgeMax { get; set; }

    public NpgsqlRange<int>? AgeRange { get; private set; }

    public short? GradeMin { get; set; }

    public short? GradeMax { get; set; }

    public NpgsqlRange<int>? GradeRange { get; private set; }

    public string? AudienceNote { get; set; }

    public string Modality { get; set; } = "in_person";

    public string? VirtualUrl { get; set; }

    public bool IsDropIn { get; set; }

    public bool MealsProvided { get; set; }

    public bool TransportProvided { get; set; }

    public string? TransportNote { get; set; }

    public string CostModel { get; set; } = "free";

    public int? CostCentsMin { get; set; }

    public int? CostCentsMax { get; set; }

    public bool? IsFree { get; private set; }

    public string? SlidingScaleNote { get; set; }

    public string RegistrationMode { get; set; } = "external";

    public string? ExternalUrl { get; set; }

    public string ExternalUrlState { get; set; } = "unchecked";

    public DateTimeOffset? ExternalUrlCheckedAt { get; set; }

    public int? CapacityTotal { get; set; }

    public bool WaitlistEnabled { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? ReviewedBy { get; set; }

    public string? ReviewNote { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? UnpublishedAt { get; set; }

    /// <summary>Scheduled publication: a published listing is not in search before this.</summary>
    public DateTimeOffset? PublishAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string Source { get; set; } = "manual";

    public string? SourceRecordId { get; set; }

    public Guid? ClonedFromId { get; set; }

    public string? SeasonLabel { get; set; }

    public Guid? HeroMediaId { get; set; }

    public string? ContactEmail { get; set; }

    public string? ContactPhone { get; set; }

    public string? AccessibilityNote { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? CreatedBy { get; init; }
}

/// <summary>A row of <c>opportunity.listing_session</c>: a schedule rule.</summary>
public sealed class ListingSessionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public Guid? SiteId { get; set; }

    public string? Label { get; set; }

    public required string Timezone { get; set; }

    public DateOnly StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }

    public string? Rrule { get; set; }

    public DateOnly[] Exdates { get; set; } = [];

    public bool IsAllDay { get; set; }

    public bool IsCancelled { get; set; }

    public DateOnly? ExpandedThrough { get; set; }
}

/// <summary>A row of <c>opportunity.listing_occurrence</c>: one materialised meeting.</summary>
public sealed class ListingOccurrenceRecord
{
    public long Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public Guid SessionId { get; init; }

    public Guid? SiteId { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    /// <summary>0 = Sunday, from the session's own timezone. See the column comment in the DDL.</summary>
    public short DayOfWeek { get; init; }

    public bool IsCancelled { get; set; }
}

/// <summary>A row of <c>opportunity.listing_search</c>, the finder's read model.</summary>
public sealed class ListingSearchRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public Guid? SiteId { get; init; }

    public Guid OrgId { get; init; }

    public required string Slug { get; init; }

    public required string Kind { get; init; }

    public bool IsPublished { get; init; }

    public string[] Facets { get; init; } = [];

    public NpgsqlRange<int>? AgeRange { get; init; }

    public NpgsqlRange<int>? GradeRange { get; init; }

    public int CostCents { get; init; }

    public bool IsFree { get; init; }

    public required string Modality { get; init; }

    public Guid? NeighbourhoodId { get; init; }

    public NetTopologySuite.Geometries.Point? Location { get; init; }

    public NpgsqlRange<DateTime>? SessionWindow { get; init; }

    public DateTimeOffset? NextStartAt { get; init; }

    public DateTimeOffset? NextDeadlineAt { get; init; }

    public bool IsDropIn { get; init; }

    /// <summary>
    /// Never built in C#: client-side tsvector parsing cannot reproduce PostgreSQL's. Omitted
    /// on insert so the column default applies, then set by SQL with <c>to_tsvector</c>.
    /// </summary>
    public NpgsqlTsVector? SearchVector { get; init; }

    public string FuzzyText { get; init; } = string.Empty;

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public float Popularity { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset ProjectedAt { get; init; }
}
