using BrightTime.SharedKernel.Geo;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>
/// One row of the search read model, as the projector computes it. Persisted to
/// <c>opportunity.listing_search</c>; one row per (listing, site), with a single
/// site-less row for virtual-only listings.
/// </summary>
public sealed record ListingSearchRow(
    Guid ListingId,
    Guid? SiteId,
    Guid OrganisationId,
    string Kind,
    bool IsPublished,
    IReadOnlyList<string> Facets,
    int? AgeMin,
    int? AgeMax,
    int? GradeMin,
    int? GradeMax,
    int CostCents,
    bool IsFree,
    string Modality,
    Guid? NeighbourhoodId,
    Coordinate? Location,
    DateTimeOffset? SessionWindowStart,
    DateTimeOffset? SessionWindowEnd,
    DateTimeOffset? NextStartAt,
    DateTimeOffset? NextDeadlineAt,
    bool IsDropIn,
    string Title,
    string Summary,
    string FuzzyText,
    DateTimeOffset? PublishedAt);
