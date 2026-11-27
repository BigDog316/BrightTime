using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Hosting.Auth;

/// <summary>
/// The clearance gate (D99) on every organisation-scoped permission check. Where the county
/// enforces clearances, a permission that acts on a named young person's record is refused to
/// anyone at the organisation, owners included, who lacks a current clearance of a required kind.
/// The permission check itself still decides everything else; this handler only ever fails.
/// </summary>
/// <remarks>
/// <para>
/// Recording clearances, editing listings and the organisation, and aggregate reports aren't
/// gated, so no one is locked out of putting it right. A report that names people needs the roster
/// permission, which is.
/// </para>
/// <para>
/// A refusal is audited once per organisation and request, naming what was missing, and left on
/// the request for a page to explain.
/// </para>
/// </remarks>
public sealed class ClearanceGateHandler(IHttpContextAccessor http) : AuthorizationHandler<OrganisationScopeRequirement, AuthorizationTarget>
{
    public const string Refused = "directory.clearance.refused";

    /// <summary>Where a refusal is left on the request, by organisation.</summary>
    public const string ItemKey = "brighttime.clearance_refusals";

    /// <summary>The permissions that act on a named young person's record.</summary>
    public static IReadOnlySet<string> Gated { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.Youth.ReadRecord, Permissions.Youth.ReadSupportNeeds,
        Permissions.Applications.View, Permissions.Applications.Decide,
        Permissions.Attendance.Record, Permissions.Attendance.ViewRoster, Permissions.Attendance.OfflineSync,
        Permissions.Messages.SendToFamily,
        Permissions.Spaces.View, Permissions.Spaces.Post, Permissions.Spaces.ManageMembership,
        Permissions.Payments.Waive,
        Permissions.Badges.Issue, Permissions.Badges.Revoke,
        Permissions.Projects.Review, Permissions.Skills.Endorse, Permissions.Playlists.ReviewSteps,
    };

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OrganisationScopeRequirement requirement, AuthorizationTarget resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(resource);
        // Only where the permission check would otherwise pass: then the clearance is the reason,
        // and that is what the refusal says. Anyone refused for their role is refused already.
        if (!Gated.Contains(requirement.Permission)
            || resource.OrganisationId is not { } organisation
            || context.User.GetUserId() is not { } user
            || !context.User.HasPermission(requirement.Permission)
            || !OrganisationRoles.IsAtLeast(context.User.GetOrganisationRole(organisation.Value), requirement.MinimumRole)
            || http.HttpContext is not { } request
            || request.RequestServices.GetService<IClearanceGate>() is not { } gate)
        {
            return;
        }

        if (await gate.CheckAsync(new UserId(user), organisation, request.RequestAborted).ConfigureAwait(false) is not { } refusal)
        {
            return;
        }

        if (request.Items[ItemKey] is not Dictionary<Guid, ClearanceRefusal> refusals)
        {
            request.Items[ItemKey] = refusals = [];
        }

        if (refusals.TryAdd(organisation.Value, refusal))
        {
            request.RequestServices.GetService<IAuditWriter>()?.Record(new AuditAccess
            {
                Action = Refused,
                Outcome = AuditOutcome.Deny,
                ActorId = new UserId(user),
                ActorOrgId = organisation,
                SubjectKind = "user",
                SubjectIds = [user],
                Reason = AuthorizationFailureReasons.ClearanceNotCurrent,
                Detail = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["permission"] = requirement.Permission,
                    ["missing"] = string.Join(',', refusal.Missing),
                    ["expired"] = string.Join(',', refusal.Expired),
                },
            });
        }

        context.Fail(AuthorizationFailureReasons.For(this, AuthorizationFailureReasons.ClearanceNotCurrent,
            "The county requires current clearances to act on young people's records, and this account lacks one."));
    }

    /// <summary>The refusal this request met at <paramref name="organisation"/>, if any.</summary>
    public static ClearanceRefusal? RefusalAt(HttpContext context, Guid organisation)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[ItemKey] is Dictionary<Guid, ClearanceRefusal> refusals && refusals.TryGetValue(organisation, out ClearanceRefusal? refusal) ? refusal : null;
    }
}
