using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Paging;

namespace BrightTime.Modules.Opportunities.Search;

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

    public PageSize PageSize { get; init; } = new(PageSize.Default);
}
