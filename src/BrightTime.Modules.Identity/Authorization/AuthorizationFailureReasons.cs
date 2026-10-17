using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// The reasons a check can fail, as stable codes the API maps to RFC 9457
/// problem types.
/// </summary>
/// <remarks>
/// Named reasons exist so that a denial can be explained. "Forbidden" is
/// useless to a parent halfway through verifying a guardian link; "your link to
/// this child has not been verified yet" is actionable. This is also why
/// per-record authorisation lives in the application rather than in an RLS
/// predicate — a policy can only return rows or no rows.
/// </remarks>
public static class AuthorizationFailureReasons
{
    public const string NoTenant = "tenant.unresolved";
    public const string WrongTenant = "tenant.mismatch";
    public const string NotAuthenticated = "actor.unauthenticated";
    public const string MissingPermission = "permission.missing";
    public const string NotOrganisationMember = "organisation.not_a_member";
    public const string InsufficientOrganisationRole = "organisation.role_insufficient";
    public const string NoYouthAccessGrant = "youth_record.no_grant";
    public const string YouthAccessGrantExpired = "youth_record.grant_expired";
    public const string FieldNotCoveredByGrant = "youth_record.field_not_granted";
    public const string GuardianLinkUnverified = "guardian.unverified";
    public const string GuardianLinkRevoked = "guardian.revoked";
    public const string NotGuardian = "guardian.not_linked";
    public const string CapabilityWithheld = "capability.withheld";
    public const string ParentalConsentMissing = "consent.missing";
    public const string ClearanceMissing = "clearance.missing";
    public const string ClearanceExpired = "clearance.expired";
    public const string ContactRestricted = "contact.restricted";
    public const string ReasonRequired = "audit.reason_required";
    public const string StepUpRequired = "authentication.step_up_required";

    public static AuthorizationFailureReason For(IAuthorizationHandler handler, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new AuthorizationFailureReason(handler, $"{code}: {message}");
    }
}
