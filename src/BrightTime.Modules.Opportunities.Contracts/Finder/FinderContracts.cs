using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>One result card. Everything a family needs to decide, and the reasons the ranking chose it.</summary>
public sealed record FinderItem(
    ListingId ListingId,
    string Slug,
    OrganisationId OrganisationId,
    ListingKind Kind,
    string Title,
    string Summary,
    int CostCents,
    bool IsFree,
    DateTimeOffset? NextStartAt,
    double? DistanceMetres,
    double Score,
    IReadOnlyList<Phrase> Reasons,
    string SortValue,
    DateTimeOffset? NextDeadlineAt = null);

/// <summary>A page of finder results. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record FinderPage(IReadOnlyList<FinderItem> Items, string? NextCursor)
{
    public static FinderPage Empty { get; } = new([], null);
}

/// <summary>
/// Searches the public finder. Anonymous, read-only, and tenant-scoped by the
/// caller's resolved tenant. Implementations must never return an unpublished
/// listing, whatever the query asks for.
/// </summary>
/// <summary>
/// How many listings a search finds in each neighbourhood, for the map. A listing at sites in
/// two neighbourhoods counts in both; <see cref="OnlineOnly"/> counts those with no site at all.
/// </summary>
public sealed record NeighbourhoodCounts(IReadOnlyDictionary<Guid, int> ByNeighbourhood, int OnlineOnly)
{
    public static NeighbourhoodCounts None { get; } = new(new Dictionary<Guid, int>(), 0);
}

public interface IListingFinder
{
    Task<FinderPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>The same filters as <see cref="SearchAsync"/>, counted by neighbourhood. Sort and paging are ignored.</summary>
    Task<NeighbourhoodCounts> CountByNeighbourhoodAsync(SearchQuery query, CancellationToken cancellationToken = default);
}
