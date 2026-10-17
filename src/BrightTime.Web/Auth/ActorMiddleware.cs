using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Web.Auth;

/// <summary>
/// Makes the signed-in person the request's actor, so row-level security and audit see who
/// is acting. Runs after authentication.
/// </summary>
/// <remarks>
/// A principal from another tenant is never honoured: the cookie validator already rejects
/// it, and this refuses the request outright if one gets this far.
/// </remarks>
public sealed class ActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);

        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (context.User.GetTenantId() != tenant.TenantId.Value || context.User.GetUserId() is not { } user)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            tenant.SetActor(new UserId(user), ActorKind.User);
        }

        await next(context).ConfigureAwait(false);
    }
}
