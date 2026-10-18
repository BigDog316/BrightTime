using System.Security.Claims;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BrightTime.Api.Auth;

/// <summary>
/// Turns a signature-checked token into the request's principal: the token must be this
/// tenant's, and the account must still be active with the stamp the token was issued under.
/// The principal itself is rebuilt from the database, as the web's cookie validator does.
/// </summary>
public static class BearerPrincipal
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IServiceProvider services = context.HttpContext.RequestServices;
        TenantContext tenant = services.GetRequiredService<TenantContext>();
        SiteContext site = services.GetRequiredService<SiteContext>();

        if (context.Principal is null || AccessTokens.Read(context.Principal) is not { } token
            || !tenant.IsResolved || token.Tenant != tenant.TenantId.Value
            || context.Principal.FindFirstValue(JwtRegisteredClaimNames.Iss) != site.BaseUrl?.AbsoluteUri)
        {
            context.Fail("The token is not this site's.");
            return;
        }

        ClaimsPrincipal? fresh = await services.GetRequiredService<BrightTimePrincipalFactory>()
            .CreateAsync(tenant.TenantId, new UserId(token.User), token.Authenticated, context.HttpContext.RequestAborted);
        if (fresh is null || fresh.FindFirstValue(BrightTimePrincipalFactory.StampClaim) != token.Stamp)
        {
            // Suspended, deleted, or signed out everywhere since the token was issued.
            context.Fail("The account has changed since the token was issued.");
            return;
        }

        context.Principal = fresh;
    }
}
