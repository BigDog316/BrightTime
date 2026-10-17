using System.Security.Claims;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace BrightTime.Web.Components.Console;

/// <summary>
/// What every console page needs: the request, the signed-in person, organisation-scoped
/// authorisation, and the services that exist only when the database does.
/// </summary>
/// <remarks>
/// Pages are static server-rendered (decision D16). A POST is handled by the page that
/// rendered the form, which reads the form itself, and a success redirects (post, redirect,
/// get) so a refresh never repeats an action.
/// </remarks>
public abstract class ConsoleComponent : ComponentBase
{
    [CascadingParameter] public HttpContext HttpContext { get; set; } = default!;

    [Inject] protected ConsoleAvailability Availability { get; set; } = default!;

    [Inject] protected IServiceProvider Services { get; set; } = default!;

    [Inject] protected IAuthorizationService Authorization { get; set; } = default!;

    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    [Inject] protected ITenantContext Tenant { get; set; } = default!;

    protected ClaimsPrincipal User => HttpContext.User;

    protected UserId Actor => new(User.GetUserId() ?? throw new InvalidOperationException("A console action needs a signed-in person."));

    protected bool IsPost => HttpMethods.IsPost(HttpContext.Request.Method);

    /// <summary>Set when the page refused the request; the markup shows why instead of the page.</summary>
    protected bool Forbidden { get; private set; }

    protected T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    protected void Forbid()
    {
        HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        Forbidden = true;
    }

    /// <summary>Permission plus a live membership of the organisation with at least the role given.</summary>
    protected async Task<bool> CanActForAsync(OrganisationId organisation, string permission, string minimumRole = OrganisationRoles.Staff)
    {
        AuthorizationResult result = await Authorization.AuthorizeAsync(
            User,
            new AuthorizationTarget { OrganisationId = organisation },
            new OrganisationScopeRequirement(permission, minimumRole));
        return result.Succeeded;
    }

    protected string FormValue(string name) => HttpContext.Request.Form[name].ToString();

    /// <summary>The signed-in person's role in <paramref name="organisation"/>, from their active memberships, or null.</summary>
    protected string? RoleIn(OrganisationId organisation)
    {
        foreach (Claim claim in User.FindAll(BrightTimeClaims.OrganisationMembership))
        {
            if (BrightTimeClaims.TryParseMembership(claim.Value, out Guid id, out string role) && id == organisation.Value)
            {
                return role;
            }
        }

        return null;
    }
}
