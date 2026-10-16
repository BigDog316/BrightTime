using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;

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
    IReadOnlyList<string> Reasons,
    string SortValue);

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
public interface IListingFinder
{
    Task<FinderPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}
