namespace BrightTime.Modules.Platform.Contracts;

/// <summary>A taxonomy term as other modules see it: its vocabulary, its stable code, and a display name.</summary>
public sealed record TermSummary(Guid Id, string Vocabulary, string Code, string? Name);

/// <summary>An active term of one vocabulary, for a picker: parent for grouping, order for display.</summary>
public sealed record VocabularyTerm(Guid Id, string Code, string Name, Guid? ParentId, short SortOrder);

/// <summary>Taxonomy lookups for other modules. Read-only and tenant-scoped by the caller's context.</summary>
public interface ITaxonomyQueries
{
    /// <summary>
    /// The named terms, with display names in <paramref name="locale"/>, falling back to English
    /// and then to none. Inactive terms are returned too: a listing tagged before a term was
    /// retired still needs its facet until it is re-tagged.
    /// </summary>
    Task<IReadOnlyList<TermSummary>> GetTermsAsync(
        IReadOnlyCollection<Guid> termIds,
        string locale,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The active terms of a vocabulary, in display order, named in <paramref name="locale"/>
    /// with English fallback and then the code. For editors' pickers; a retired term is not offered.
    /// </summary>
    Task<IReadOnlyList<VocabularyTerm>> GetVocabularyAsync(
        string vocabulary,
        string locale,
        CancellationToken cancellationToken = default);
}

/// <summary>Place-name lookups for other modules.</summary>
public interface IGeographyQueries
{
    /// <summary>Neighbourhood names in <paramref name="locale"/>, falling back to English.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNeighbourhoodNamesAsync(
        IReadOnlyCollection<Guid> neighbourhoodIds,
        string locale,
        CancellationToken cancellationToken = default);
}
