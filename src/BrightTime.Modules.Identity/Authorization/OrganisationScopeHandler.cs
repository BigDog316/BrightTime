using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// Permission plus membership. A provider admin at one organisation is a member
/// of the public at every other, and this is the handler that says so.
/// </summary>
public sealed class OrganisationScopeHandler : AuthorizationHandler<OrganisationScopeRequirement, AuthorizationTarget>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OrganisationScopeRequirement requirement,
        AuthorizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(target);

        if (!context.User.HasPermission(requirement.Permission))
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.MissingPermission,
                $"This account does not have the '{requirement.Permission}' permission."));
            return Task.CompletedTask;
        }

        if (target.OrganisationId is not { } organisationId)
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.NotOrganisationMember,
                "This action needs an organisation, and none was supplied."));
            return Task.CompletedTask;
        }

        string? heldRole = context.User.GetOrganisationRole(organisationId.Value);
        if (heldRole is null)
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.NotOrganisationMember,
                "This account is not a member of that organisation."));
            return Task.CompletedTask;
        }

        if (!OrganisationRoles.IsAtLeast(heldRole, requirement.MinimumRole))
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.InsufficientOrganisationRole,
                $"This action needs the '{requirement.MinimumRole}' role or higher; this account holds '{heldRole}'."));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
