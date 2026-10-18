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

/// <summary>
/// A site was edited, closed or reopened. Modules that copy a site's place or name (the
/// listing search rows) refresh it. Not published for a new site: nothing is held there yet.
/// </summary>
public sealed record SiteChanged(
    OrganisationId OrganisationId,
    Guid SiteId,
    Guid? NeighbourhoodId,
    bool IsActive,
    DateTimeOffset ChangedAt) : IModuleMessage
{
    public static string MessageType => "directory.site.changed";
}

/// <summary>
/// An organisation's vetting status changed, by an organiser's decision or the provider's own
/// move. Its listings are findable only while it is approved, so search re-projects them.
/// <see cref="OrganisationApproved"/> and <see cref="OrganisationSuspended"/> are published as
/// well where they apply, for the subscribers that care about those moves alone.
/// </summary>
public sealed record OrganisationStatusChanged(
    OrganisationId OrganisationId,
    OrganisationStatus From,
    OrganisationStatus To,
    DateTimeOffset ChangedAt) : IModuleMessage
{
    public static string MessageType => "directory.organisation.status_changed";
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
