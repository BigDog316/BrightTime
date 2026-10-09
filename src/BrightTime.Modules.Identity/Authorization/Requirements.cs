using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>The request's tenant matches the token's tenant and the resolved session tenant.</summary>
public sealed class TenantRequirement : IAuthorizationRequirement;

/// <summary>The actor's role set grants a named permission.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// The actor holds a permission <em>and</em> a live membership in the target
/// organisation with sufficient role.
/// </summary>
public sealed class OrganisationScopeRequirement(string permission, string minimumRole)
    : IAuthorizationRequirement
{
    public string Permission { get; } = permission;

    /// <summary>One of the <c>ck_org_membership_role</c> values, ordered owner &gt; admin &gt; staff &gt; mentor &gt; viewer.</summary>
    public string MinimumRole { get; } = minimumRole;
}

/// <summary>
/// The actor's organisation has a live, reason-bearing grant over this young
/// person's record, covering the fields requested.
/// </summary>
/// <remarks>
/// Failure is a 403 carrying the reason, not an empty result set. An empty
/// result hides the probe and tells an honest caller nothing; a young person
/// whose guardian has not finished verifying should be told that, not shown a
/// blank page.
/// </remarks>
public sealed class YouthRecordRequirement(params string[] fields) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Fields { get; } = fields;
}

/// <summary>The actor is a verified, unrevoked guardian of the target child.</summary>
public sealed class GuardianOfRequirement : IAuthorizationRequirement;

/// <summary>The actor is the subject, or a verified guardian of it.</summary>
public sealed class SelfOrGuardianRequirement : IAuthorizationRequirement;

/// <summary>The actor's youth account currently holds a capability. This is the COPPA gate at the endpoint.</summary>
public sealed class CapabilityRequirement(YouthCapability capability) : IAuthorizationRequirement
{
    public YouthCapability Capability { get; } = capability;
}

/// <summary>
/// Every named clearance is recorded as cleared and unexpired.
/// </summary>
/// <remarks>
/// Checked on each youth-facing action, not only when an assignment is
/// created. A mentor whose background check lapses overnight loses youth-facing
/// access by morning without anyone intervening, which is the only version of
/// this that works in practice.
/// </remarks>
public sealed class ClearanceCurrentRequirement(params string[] clearanceKinds) : IAuthorizationRequirement
{
    public IReadOnlyList<string> ClearanceKinds { get; } = clearanceKinds;
}

/// <summary>No contact restriction exists between the actor and the subject, in either direction.</summary>
public sealed class NoRestrictedContactRequirement : IAuthorizationRequirement;

/// <summary>
/// The resource an authorisation check is about. Passed as the resource
/// argument so handlers never have to guess at route values.
/// </summary>
public sealed record AuthorizationTarget
{
    public UserId? SubjectUserId { get; init; }

    public OrganisationId? OrganisationId { get; init; }

    public Guid? ResourceId { get; init; }

    /// <summary>Supplied for a break-glass read. <c>ck_audit_reason_required</c> means a deny without one cannot even be logged.</summary>
    public string? Reason { get; init; }
}
