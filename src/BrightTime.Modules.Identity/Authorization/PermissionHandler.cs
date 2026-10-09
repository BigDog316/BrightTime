using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>Checks a flat permission claim. The cheapest gate, and never the only one for youth data.</summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.NotAuthenticated,
                "Sign in to continue."));
            return Task.CompletedTask;
        }

        bool granted = context.User.HasClaim(
            c => string.Equals(c.Type, BrightTimeClaims.Permission, StringComparison.Ordinal)
                 && string.Equals(c.Value, requirement.Permission, StringComparison.Ordinal));

        if (granted)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.MissingPermission,
                $"This account does not have the '{requirement.Permission}' permission."));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Reads BrightTime's claims off a principal.</summary>
public static class PrincipalExtensions
{
    public static Guid? GetTenantId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? value = principal.FindFirstValue(BrightTimeClaims.TenantId);
        return Guid.TryParse(value, out Guid id) ? id : null;
    }

    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out Guid id) ? id : null;
    }

    public static string? GetAccountKind(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(BrightTimeClaims.AccountKind);
    }

    /// <summary>The organisation role held in one organisation, or null for no membership.</summary>
    public static string? GetOrganisationRole(this ClaimsPrincipal principal, Guid organisationId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        foreach (Claim claim in principal.FindAll(BrightTimeClaims.OrganisationMembership))
        {
            if (BrightTimeClaims.TryParseMembership(claim.Value, out Guid id, out string role)
                && id == organisationId)
            {
                return role;
            }
        }

        return null;
    }

    public static bool HasPermission(this ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.HasClaim(
            c => string.Equals(c.Type, BrightTimeClaims.Permission, StringComparison.Ordinal)
                 && string.Equals(c.Value, permission, StringComparison.Ordinal));
    }
}
