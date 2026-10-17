using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Directory.Contracts;

/// <summary>A live membership of an organisation.</summary>
public sealed record OrganisationMembership(
    OrganisationId OrganisationId,
    UserId UserId,
    string OrgRole,
    DateTimeOffset JoinedAt);

/// <summary>A clearance's status and expiry. Nothing else, by design.</summary>
public sealed record ClearanceState(
    ClearanceKind Kind,
    ClearanceStatus Status,
    DateOnly? ExpiresOn)
{
    /// <summary>
    /// Cleared or conditional, and not past its expiry. A lapsed background
    /// check is not "cleared but stale" — it is not cleared.
    /// </summary>
    public bool IsCurrentOn(DateOnly asOf) =>
        Status is ClearanceStatus.Cleared or ClearanceStatus.Conditional
        && (ExpiresOn is null || ExpiresOn >= asOf);
}

/// <summary>
/// A site as other modules see it. The street address and the transit note are included
/// only when <see cref="IsAddressPublic"/> is true; otherwise they are null and only the
/// neighbourhood identifies where it is. The location is always present for distance
/// search, which never discloses it.
/// </summary>
public sealed record SiteSummary(
    Guid SiteId,
    OrganisationId OrganisationId,
    string Name,
    Guid? NeighbourhoodId,
    Coordinate? Location,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? TransitNote,
    bool? IsWheelchairAccessible,
    bool IsActive);

/// <summary>What other modules may ask Directory.</summary>
public interface IDirectoryQueries
{
    Task<IReadOnlyList<OrganisationMembership>> GetMembershipsAsync(
        UserId userId,
        CancellationToken cancellationToken = default);

    Task<string?> GetOrganisationRoleAsync(
        OrganisationId organisationId,
        UserId userId,
        CancellationToken cancellationToken = default);

    /// <summary>The sites named, active or not. Callers decide what an inactive site means for them.</summary>
    Task<IReadOnlyList<SiteSummary>> GetSitesAsync(
        IReadOnlyCollection<Guid> siteIds,
        CancellationToken cancellationToken = default);

    Task<string?> GetOrganisationNameAsync(
        OrganisationId organisationId,
        CancellationToken cancellationToken = default);

    Task<OrganisationStatus?> GetStatusAsync(
        OrganisationId organisationId,
        CancellationToken cancellationToken = default);

    /// <summary>Clearance states for one person at one organisation, plus any tenant-wide ones.</summary>
    Task<IReadOnlyList<ClearanceState>> GetClearancesAsync(
        UserId userId,
        OrganisationId? organisationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether every named clearance is current. The question the
    /// authorisation layer actually asks before letting an adult near youth.
    /// </summary>
    Task<bool> HasCurrentClearancesAsync(
        UserId userId,
        OrganisationId organisationId,
        IReadOnlyList<ClearanceKind> required,
        CancellationToken cancellationToken = default);
}
