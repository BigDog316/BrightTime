using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Paging;

namespace BrightTime.Modules.Opportunities.Contracts;

public enum SearchSort
{
    Relevance,
    Soonest,
    Nearest,
    Newest,
}

/// <summary>
/// Everything a caller may ask of the finder. Every field is optional except the
/// tenant, and an absent field adds no predicate to the query.
/// </summary>
public sealed record SearchQuery
{
    public required Guid TenantId { get; init; }

    /// <summary>Free text. Parameterised, never concatenated into SQL.</summary>
    public string? Text { get; init; }

    /// <summary>The PostgreSQL text-search configuration for the locale, e.g. <c>platform.ts_en</c>.</summary>
    public string TextSearchConfig { get; init; } = SharedKernel.Primitives.Locale.TextSearchSimple;

    public IReadOnlyList<string>? AllFacets { get; init; }

    public IReadOnlyList<string>? AnyFacets { get; init; }

    public IReadOnlyList<string>? Kinds { get; init; }

    /// <summary>Only this organisation's listings, for its page.</summary>
    public Guid? OrganisationId { get; init; }

    /// <summary>Only these listings, for a collection or a saved list. Unpublished ones are left out.</summary>
    public IReadOnlyList<Guid>? ListingIds { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public int? MaxCostCents { get; init; }

    public bool FreeOnly { get; init; }

    public Coordinate? Origin { get; init; }

    public Distance? Radius { get; init; }

    /// <summary>The viewer's declared interests, as facet tokens. Feeds the explainable boost only.</summary>
    public IReadOnlyList<string>? InterestTokens { get; init; }

    public SearchSort Sort { get; init; } = SearchSort.Relevance;

    /// <summary>The keyset position after the previous page, or null for the first.</summary>
    public Cursor? After { get; init; }

    /// <summary>
    /// The instant relevance and "days until it starts" are measured at, for the first page. A
    /// cursor's own instant wins over it, and with neither, finders use now. Finders carry the
    /// first page's instant in the cursor, so a score can't drift between pages (see
    /// <see cref="Cursor"/>).
    /// </summary>
    public DateTimeOffset? AsOf { get; init; }

    public PageSize PageSize { get; init; } = new(PageSize.Default);
}
