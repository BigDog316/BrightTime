using System.Security.Claims;
using BrightTime.SharedKernel.Ids;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace BrightTime.Web.Auth;

/// <summary>
/// Re-issues the sign-in cookie from the account's current state, so a membership just
/// accepted, or a role just granted, applies on the next page rather than at the next
/// revalidation.
/// </summary>
/// <remarks>
/// Keeps the cookie's properties (its persistence and the original authentication time): this
/// is the same sign-in, brought up to date, not a new one. Must run before the response starts.
/// </remarks>
public static class SessionRefresh
{
    public static async Task RefreshAsync(HttpContext http, TenantId tenant, UserId user)
    {
        ArgumentNullException.ThrowIfNull(http);

        AuthenticateResult current = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (!current.Succeeded || current.Principal is null || http.RequestServices.GetService<BrightTimePrincipalFactory>() is not { } factory)
        {
            return;
        }

        ClaimsPrincipal? fresh = await factory.CreateAsync(tenant, user, AuthenticationEvent.From(current.Principal), http.RequestAborted).ConfigureAwait(false);
        if (fresh is not null)
        {
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, fresh, current.Properties).ConfigureAwait(false);
            http.User = fresh;
        }
    }
}
