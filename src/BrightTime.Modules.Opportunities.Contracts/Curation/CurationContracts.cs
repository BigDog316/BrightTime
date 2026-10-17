namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>
/// A published collection, in the reader's language where it has been translated.
/// <c>ContentLanguage</c> is the language the title and description are in, which may not be the reader's.
/// </summary>
public sealed record CollectionView(
    Guid Id,
    string Slug,
    string Title,
    string? Description,
    string ContentLanguage,
    IReadOnlyList<CollectionEntry> Entries);

/// <summary>A listing in a collection, with the curator's note on why it is there.</summary>
public sealed record CollectionEntry(Guid ListingId, string? CuratorNote);

/// <summary>A collection as a link: home page, collection index, a neighbourhood page.</summary>
public sealed record CollectionSummary(Guid Id, string Slug, string Title, string? Description, string ContentLanguage, int ListingCount);

/// <summary>
/// A published neighbourhood page's blocks, in one language: the reader's if the organiser
/// wrote it, otherwise English (<c>ContentLanguage</c>).
/// </summary>
public sealed record NeighbourhoodPageView(Guid NeighbourhoodId, string ContentLanguage, IReadOnlyList<PageBlock> Blocks);

/// <summary>
/// One block. <see cref="Kind"/> is one of <c>ck_nhp_block_kind</c>'s values; <see cref="Payload"/>
/// is JSON whose shape depends on it (see the page component).
/// </summary>
public sealed record PageBlock(string Kind, string? Heading, string? Body, string Payload);

public interface ICurationQueries
{
    Task<CollectionView?> GetCollectionAsync(string slug, string locale, CancellationToken cancellationToken = default);

    /// <summary>Published, in date, in the organiser's order. <paramref name="featuredOnly"/> for the home page.</summary>
    Task<IReadOnlyList<CollectionSummary>> ListCollectionsAsync(string locale, bool featuredOnly, CancellationToken cancellationToken = default);

    Task<NeighbourhoodPageView?> GetNeighbourhoodPageAsync(Guid neighbourhoodId, string locale, CancellationToken cancellationToken = default);
}

/// <summary>Sample data has no curation.</summary>
public sealed class NoCuration : ICurationQueries
{
    public Task<CollectionView?> GetCollectionAsync(string slug, string locale, CancellationToken cancellationToken = default) =>
        Task.FromResult<CollectionView?>(null);

    public Task<IReadOnlyList<CollectionSummary>> ListCollectionsAsync(string locale, bool featuredOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollectionSummary>>([]);

    public Task<NeighbourhoodPageView?> GetNeighbourhoodPageAsync(Guid neighbourhoodId, string locale, CancellationToken cancellationToken = default) =>
        Task.FromResult<NeighbourhoodPageView?>(null);
}
