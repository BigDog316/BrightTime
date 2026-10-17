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
                // With a body, so it is not re-executed as "not found".
                if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
                {
                    pages.Enabled = false;
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("This session belongs to a different site. Sign out and sign in again.").ConfigureAwait(false);
                return;
            }

            tenant.SetActor(new UserId(user), ActorKind.User);
        }

        await next(context).ConfigureAwait(false);
    }
}
