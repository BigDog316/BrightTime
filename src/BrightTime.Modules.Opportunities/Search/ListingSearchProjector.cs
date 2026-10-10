using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Geo;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>Everything the projector needs about one listing, gathered by the projection job.</summary>
public sealed record ProjectionSource
{
    public required Guid ListingId { get; init; }

    public required Guid OrganisationId { get; init; }

    public required string OrganisationName { get; init; }

    public required bool IsPublished { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public required FacetInput Facets { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    /// <summary>Category display names, folded into the fuzzy text so "robotics" finds a "STEM" listing.</summary>
    public IReadOnlyList<string> CategoryNames { get; init; } = [];

    public IReadOnlyList<SiteSource> Sites { get; init; } = [];

    /// <summary>Every materialised occurrence, across all sessions.</summary>
    public IReadOnlyList<DateTimeOffset> OccurrenceStarts { get; init; } = [];

    public IReadOnlyList<DateTimeOffset> OccurrenceEnds { get; init; } = [];

    public IReadOnlyList<DateTimeOffset> Deadlines { get; init; } = [];

    /// <summary>Saves and views, decayed upstream. Feeds the ranking only.</summary>
    public double Popularity { get; init; }

    public int CostCents { get; init; }

    public bool IsFree { get; init; }
}

public sealed record SiteSource(Guid SiteId, Guid? NeighbourhoodId, Coordinate? Location);

/// <summary>
/// Turns a listing into its search rows. Pure, so the projection logic is tested
/// without a database and the job that persists the output stays thin.
/// </summary>
public static class ListingSearchProjector
{
    public static IReadOnlyList<ListingSearchRow> Project(ProjectionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        IReadOnlyList<string> facets = ListingFacets.Compute(source.Facets with
        {
            NeighbourhoodIds = [.. source.Sites.Where(s => s.NeighbourhoodId is not null).Select(s => s.NeighbourhoodId!.Value).Distinct()],
        });

        DateTimeOffset? nextStart = source.OccurrenceStarts.Count == 0
            ? null
            : source.OccurrenceStarts.Min();

        DateTimeOffset? windowStart = source.OccurrenceStarts.Count == 0 ? null : source.OccurrenceStarts.Min();
        DateTimeOffset? windowEnd = source.OccurrenceEnds.Count == 0 ? null : source.OccurrenceEnds.Max();
        DateTimeOffset? nextDeadline = source.Deadlines.Count == 0 ? null : source.Deadlines.Min();

        string fuzzy = string.Join(' ', new[] { source.Title, source.OrganisationName }
            .Concat(source.CategoryNames)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        // Virtual-only listings have no sites and get a single row with no site.
        List<SiteSource?> placements = source.Sites.Count == 0
            ? new List<SiteSource?> { null }
            : [.. source.Sites.Select(s => (SiteSource?)s)];

        return [.. placements.Select(site => new ListingSearchRow(
            ListingId: source.ListingId,
            SiteId: site?.SiteId,
            OrganisationId: source.OrganisationId,
            Kind: source.Facets.Kind.ToDbValue(),
            IsPublished: source.IsPublished,
            Facets: facets,
            AgeMin: source.Facets.AgeMin,
            AgeMax: source.Facets.AgeMax,
            GradeMin: source.Facets.GradeMin,
            GradeMax: source.Facets.GradeMax,
            CostCents: source.CostCents,
            IsFree: source.IsFree,
            Modality: source.Facets.Modality.ToDbValue(),
            NeighbourhoodId: site?.NeighbourhoodId,
            Location: site?.Location,
            SessionWindowStart: windowStart,
            SessionWindowEnd: windowEnd,
            NextStartAt: nextStart,
            NextDeadlineAt: nextDeadline,
            IsDropIn: source.Facets.IsDropIn,
            Title: source.Title,
            Summary: source.Summary,
            FuzzyText: fuzzy,
            PublishedAt: source.PublishedAt))];
    }
}
