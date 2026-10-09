using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Modules.Directory.Contracts.Messages;

/// <summary>An organisation was approved and may now publish listings.</summary>
public sealed record OrganisationApproved(
    OrganisationId OrganisationId,
    string DisplayName,
    UserId ApprovedBy,
    DateTimeOffset ApprovedAt) : IModuleMessage
{
    public static string MessageType => "directory.organisation.approved";
}

/// <summary>
/// An organisation was suspended. Handlers must unpublish its listings and
/// close its youth-access grants; a suspended provider keeps its records but
/// loses its reach.
/// </summary>
public sealed record OrganisationSuspended(
    OrganisationId OrganisationId,
    string Reason,
    UserId SuspendedBy,
    DateTimeOffset SuspendedAt) : IModuleMessage
{
    public static string MessageType => "directory.organisation.suspended";
}

/// <summary>A site moved or was created, so the search projection needs its geography refreshed.</summary>
public sealed record SiteLocationChanged(
    OrganisationId OrganisationId,
    Guid SiteId,
    Guid? NeighbourhoodId,
    DateTimeOffset ChangedAt) : IModuleMessage
{
    public static string MessageType => "directory.site.location_changed";
}

/// <summary>
/// A staff membership ended. Handlers revoke session claims, purge offline
/// rosters on that person's devices, and close their youth-access.
/// </summary>
public sealed record OrganisationMembershipEnded(
    OrganisationId OrganisationId,
    UserId UserId,
    string? Reason,
    DateTimeOffset EndedAt) : IModuleMessage
{
    public static string MessageType => "directory.org_membership.ended";
}

/// <summary>
/// A clearance lapsed or was revoked. This is the message that takes a mentor
/// out of youth-facing work overnight without anyone having to notice.
/// </summary>
public sealed record ClearanceLapsed(
    UserId UserId,
    OrganisationId? OrganisationId,
    ClearanceKind Kind,
    DateOnly LapsedOn) : IModuleMessage
{
    public static string MessageType => "directory.clearance.lapsed";
}
