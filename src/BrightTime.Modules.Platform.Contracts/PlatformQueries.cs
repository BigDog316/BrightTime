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

/// <summary>
/// A neighbourhood's outline for the public map: rings of (longitude, latitude), outer first,
/// already simplified. A boundary drawn by the organiser, never a site's location.
/// </summary>
public sealed record NeighbourhoodShape(
    Guid Id,
    string Code,
    string Name,
    IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>> Rings,
    double CentroidLongitude,
    double CentroidLatitude);

/// <summary>Place-name lookups for other modules.</summary>
public interface IGeographyQueries
{
    /// <summary>The tenant's neighbourhoods (the smallest areas, not towns or the county), named in <paramref name="locale"/>.</summary>
    Task<IReadOnlyList<NeighbourhoodShape>> GetNeighbourhoodsAsync(
        string locale,
        CancellationToken cancellationToken = default);

    /// <summary>Neighbourhood names in <paramref name="locale"/>, falling back to English.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNeighbourhoodNamesAsync(
        IReadOnlyCollection<Guid> neighbourhoodIds,
        string locale,
        CancellationToken cancellationToken = default);
}

/// <summary>Sample data has no map: no neighbourhood shapes, no names beyond the listings' own.</summary>
public sealed class NoGeography : IGeographyQueries
{
    public Task<IReadOnlyList<NeighbourhoodShape>> GetNeighbourhoodsAsync(string locale, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NeighbourhoodShape>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> GetNeighbourhoodNamesAsync(
        IReadOnlyCollection<Guid> neighbourhoodIds,
        string locale,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>Per-tenant switches (<c>platform.feature_flag</c>). An absent flag is off.</summary>
public interface IFeatureFlags
{
    Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>The flags the code reads. Keys are stable: tenants set them by name.</summary>
public static class FeatureFlagKeys
{
    /// <summary>Approving an organisation needs a current certificate of insurance on the vetting checklist.</summary>
    public const string VettingInsuranceRequired = "vetting.insurance_required";
}
