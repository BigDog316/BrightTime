using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Directory.Contracts;

/// <summary>An approved organisation as the public sees it.</summary>
public sealed record OrganisationProfile(
    OrganisationId Id,
    string Slug,
    string DisplayName,
    string Kind,
    string? Mission,
    string? WebsiteUrl,
    string? PublicEmail,
    string? PublicPhone,
    IReadOnlyList<string> LanguagesServed,
    IReadOnlyList<PublicSite> Sites);

/// <summary>
/// A site as the public sees it. No coordinates, ever; and for a site whose address is not
/// public, no address or transit note either, only its neighbourhood.
/// </summary>
public sealed record PublicSite(
    string Name,
    Guid? NeighbourhoodId,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? TransitNote,
    bool? IsWheelchairAccessible,
    bool? HasAccessibleRestroom,
    bool? HasElevator,
    bool? HasQuietSpace);

/// <summary>An approved organisation's name and page address, for linking to it.</summary>
public sealed record OrganisationLink(OrganisationId Id, string Slug, string DisplayName);

/// <summary>Public organisation pages. Only approved organisations have one.</summary>
public interface IOrganisationProfiles
{
    Task<OrganisationProfile?> GetAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Links for the approved organisations among <paramref name="ids"/>; the rest are absent.</summary>
    Task<IReadOnlyDictionary<OrganisationId, OrganisationLink>> GetLinksAsync(
        IReadOnlyCollection<OrganisationId> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>Sample data has no organisation pages.</summary>
public sealed class NoOrganisationProfiles : IOrganisationProfiles
{
    public Task<OrganisationProfile?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
        Task.FromResult<OrganisationProfile?>(null);

    public Task<IReadOnlyDictionary<OrganisationId, OrganisationLink>> GetLinksAsync(
        IReadOnlyCollection<OrganisationId> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<OrganisationId, OrganisationLink>>(new Dictionary<OrganisationId, OrganisationLink>());
}
