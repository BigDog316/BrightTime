using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// The gate on youth records: a permission, a live grant, and the fields the
/// grant actually covers.
/// </summary>
/// <remarks>
/// <para>
/// Every outcome is audited, including the denials. A provider probing for
/// youth it never served produces a trail; an authorised read produces a trail
/// naming the basis. That is what makes "who read my record, and why"
/// answerable on the young person's own transparency screen.
/// </para>
/// <para>
/// A denial is a 403 with a reason, not an empty collection. Hiding the
/// boundary teaches providers that the system is broken and tells an attacker
/// nothing less.
/// </para>
/// </remarks>
public sealed class YouthRecordHandler(
    IIdentityQueries identity,
    IAuditWriter audit,
    IClock clock) : AuthorizationHandler<YouthRecordRequirement, AuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        YouthRecordRequirement requirement,
        AuthorizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(target);

        if (target.SubjectUserId is not { } subject)
        {
            Deny(context, AuthorizationFailureReasons.NoYouthAccessGrant,
                "This action needs a subject, and none was supplied.");
            return;
        }

        // The young person themselves, and their verified guardian, always read
        // their own record. No grant is involved.
        Guid? actorId = context.User.GetUserId();
        if (actorId == subject.Value)
        {
            context.Succeed(requirement);
            return;
        }

        if (actorId is { } actor
            && await identity.IsVerifiedGuardianOfAsync(new UserId(actor), subject).ConfigureAwait(false))
        {
            context.Succeed(requirement);
            return;
        }

        bool fieldsIncludeSupportNeeds = requirement.Fields.Contains(
            YouthRecordFields.SupportNeeds, StringComparer.Ordinal);

        string requiredPermission = fieldsIncludeSupportNeeds
            ? Permissions.Youth.ReadSupportNeeds
            : Permissions.Youth.ReadRecord;

        if (!context.User.HasPermission(requiredPermission))
        {
            await DenyAndAuditAsync(
                context, subject, target,
                AuthorizationFailureReasons.MissingPermission,
                $"This account does not have the '{requiredPermission}' permission.").ConfigureAwait(false);
            return;
        }

        if (target.OrganisationId is not { } organisationId)
        {
            await DenyAndAuditAsync(
                context, subject, target,
                AuthorizationFailureReasons.NoYouthAccessGrant,
                "Reading a young person's record needs the organisation it is being read on behalf of.")
                .ConfigureAwait(false);
            return;
        }

        if (await identity.IsContactRestrictedAsync(
                new UserId(actorId ?? Guid.Empty), subject).ConfigureAwait(false))
        {
            await DenyAndAuditAsync(
                context, subject, target,
                AuthorizationFailureReasons.ContactRestricted,
                "A contact restriction is in place for this young person.").ConfigureAwait(false);
            return;
        }

        YouthAccessGrantSummary? grant = await identity
            .GetYouthAccessAsync(organisationId, subject).ConfigureAwait(false);

        if (grant is null)
        {
            await DenyAndAuditAsync(
                context, subject, target,
                AuthorizationFailureReasons.NoYouthAccessGrant,
                "This organisation has no current relationship with this young person.").ConfigureAwait(false);
            return;
        }

        if (grant.EndsAt is { } endsAt && endsAt <= clock.UtcNow)
        {
            await DenyAndAuditAsync(
                context, subject, target,
                AuthorizationFailureReasons.YouthAccessGrantExpired,
                $"This organisation's access ended on {endsAt:yyyy-MM-dd}. Aggregate reports remain available.")
                .ConfigureAwait(false);
            return;
        }

        // An empty Fields list on the grant means the default record set.
        // Anything beyond that has to be named explicitly.
        if (grant.Fields.Count > 0)
        {
            string? uncovered = requirement.Fields
                .FirstOrDefault(f => !grant.Fields.Contains(f, StringComparer.Ordinal));

            if (uncovered is not null)
            {
                await DenyAndAuditAsync(
                    context, subject, target,
                    AuthorizationFailureReasons.FieldNotCoveredByGrant,
                    $"This organisation's access does not cover '{uncovered}'.").ConfigureAwait(false);
                return;
            }
        }

        audit.Record(new AuditAccess
        {
            Action = "identity.youth_record.read",
            Outcome = AuditOutcome.Allow,
            SubjectKind = "youth",
            SubjectIds = [subject.Value],
            ActorOrgId = organisationId,
            Reason = target.Reason,
            Detail = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["basis"] = grant.Basis.ToDbValue(),
                ["fields"] = string.Join(',', requirement.Fields),
            },
        });

        context.Succeed(requirement);
    }

    private void Deny(AuthorizationHandlerContext context, string code, string message) =>
        context.Fail(AuthorizationFailureReasons.For(this, code, message));

    private async Task DenyAndAuditAsync(
        AuthorizationHandlerContext context,
        UserId subject,
        AuthorizationTarget target,
        string code,
        string message)
    {
        audit.Record(new AuditAccess
        {
            Action = "identity.youth_record.read",
            Outcome = AuditOutcome.Deny,
            SubjectKind = "youth",
            SubjectIds = [subject.Value],
            ActorOrgId = target.OrganisationId,
            // ck_audit_reason_required: a deny cannot be written without one,
            // so the failure code is the reason when the caller gave none.
            Reason = target.Reason ?? code,
        });

        await audit.FlushAsync().ConfigureAwait(false);

        Deny(context, code, message);
    }
}

/// <summary>
/// The field groups a youth-record grant can cover. Named so an endpoint
/// declares what it needs and the grant can be narrower than the role.
/// </summary>
public static class YouthRecordFields
{
    public const string Identity = "identity";
    public const string Contact = "contact";
    public const string Attendance = "attendance";
    public const string Enrollment = "enrollment";

    /// <summary>Health, disability, IEP/504. Needs the grant, the permission, and a family-profile share.</summary>
    public const string SupportNeeds = "support_needs";

    public const string Participation = "participation";
}
