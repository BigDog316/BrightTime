using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Modules.Identity.Contracts.Messages;

/// <summary>A guardian link reached verified state, so the guardian may now act for the child.</summary>
public sealed record GuardianLinkVerified(
    UserId GuardianUserId,
    UserId ChildUserId,
    ConsentMethod VerificationMethod,
    DateTimeOffset VerifiedAt) : IModuleMessage
{
    public static string MessageType => "identity.guardian_link.verified";
}

/// <summary>A scoped consent was granted. Capability recomputation and a confirmation to the guardian follow.</summary>
public sealed record ParentalConsentGranted(
    UserId ChildUserId,
    UserId GuardianUserId,
    ConsentScope Scope,
    ConsentMethod Method,
    DateTimeOffset GrantedAt) : IModuleMessage
{
    public static string MessageType => "identity.parental_consent.granted";
}

/// <summary>
/// A consent was revoked. Handlers must treat this as immediate: capability is
/// recomputed, community content by the child is unpublished where the revoked
/// scope covered it, and pending SMS is cancelled.
/// </summary>
public sealed record ParentalConsentRevoked(
    UserId ChildUserId,
    UserId GuardianUserId,
    ConsentScope Scope,
    DateTimeOffset RevokedAt,
    string? Reason) : IModuleMessage
{
    public static string MessageType => "identity.parental_consent.revoked";
}

/// <summary>A youth or child account's capability set changed.</summary>
public sealed record YouthCapabilityChanged(
    UserId UserId,
    YouthCapabilities Capabilities,
    DateTimeOffset ChangedAt) : IModuleMessage
{
    public static string MessageType => "identity.youth_capability.changed";
}

/// <summary>
/// A contact restriction was created. Every module holding a conversation,
/// space membership or roster must apply it in both directions.
/// </summary>
public sealed record ContactRestrictionCreated(
    UserId ProtectedUserId,
    UserId? RestrictedUserId,
    OrganisationId? RestrictedOrganisationId,
    string ReasonKind,
    DateTimeOffset CreatedAt) : IModuleMessage
{
    public static string MessageType => "identity.restricted_contact.created";
}

/// <summary>A data-subject deletion request completed, so denormalised copies must be purged.</summary>
public sealed record SubjectDataDeleted(
    UserId UserId,
    DateTimeOffset CompletedAt) : IModuleMessage
{
    public static string MessageType => "identity.subject.deleted";
}
