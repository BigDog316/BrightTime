using System.Security.Claims;
using System.Text.Json;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Auditing;
using BrightTime.Web.Localisation;
using Fido2NetLib;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;

namespace BrightTime.Web.Auth;

/// <summary>
/// Step-up authentication (threat model T21): sensitive actions need the person to have
/// proved who they are within the last few minutes, not just to hold a session.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint opts in with <see cref="Policy"/>. When it fails, a page request is sent to
/// <see cref="ConfirmPage"/> and comes back after; a JSON request gets a 403 that says where to
/// confirm. Confirming with a password or one of the account's own passkeys reissues the
/// session cookie with a new authentication time. Nothing else changes in the session.
/// </para>
/// <para>
/// An account signed in only through an external provider has neither to offer. Re-running
/// the provider's flow with <c>prompt=login</c> would work, but providers honour it unevenly,
/// so such a person sets a password first (by emailed link) and confirms with that.
/// </para>
/// </remarks>
public static class StepUp
{
    public const string Policy = "authentication.recent";
    public const string ConfirmPage = "/account/confirm";
    public const string PasskeyOptionsPath = "/account/confirm/passkey/options";
    public const string PasskeyPath = "/account/confirm/passkey";

    private const string StateCookie = "brighttime.stepup";

    public static string ConfirmPath(string? returnUrl) =>
        string.IsNullOrEmpty(returnUrl) ? ConfirmPage : $"{ConfirmPage}?returnUrl={Uri.EscapeDataString(returnUrl)}";

    /// <summary>Reissues the session with a fresh authentication time, after a successful confirmation.</summary>
    public static async Task<bool> ReissueAsync(HttpContext http, string method)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.User.GetUserId() is not { } user)
        {
            return false;
        }

        IServiceProvider services = http.RequestServices;
        ClaimsPrincipal? principal = await services.GetRequiredService<BrightTimePrincipalFactory>().CreateAsync(
            services.GetRequiredService<ITenantContext>().TenantId,
            new UserId(user),
            new AuthenticationEvent(method, services.GetRequiredService<IClock>().UtcNow),
            http.RequestAborted).ConfigureAwait(false);
        if (principal is null)
        {
            return false;
        }

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
        http.User = principal;
        return true;
    }

    public static IEndpointRouteBuilder MapStepUp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(PasskeyOptionsPath, async (HttpContext http, IAntiforgery antiforgery, Passkeys passkeys, ITenantContext tenant, Text text) =>
        {
            if (!await ValidAsync(http, antiforgery).ConfigureAwait(false) || http.User.GetUserId() is not { } user)
            {
                return Results.BadRequest();
            }

            AssertionOptions options = await passkeys.BeginConfirmAsync(tenant.TenantId, new UserId(user), http.RequestAborted).ConfigureAwait(false);
            if (options.AllowCredentials.Count == 0)
            {
                return Results.BadRequest(new { error = text["account.passkey.none_on_account"] });
            }

            http.Response.Cookies.Append(StateCookie, Protector(http, user).Protect(options.ToJson(), TimeSpan.FromMinutes(5)), new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromMinutes(5),
                Path = "/",
            });
            return Results.Content(options.ToJson(), "application/json");
        }).RequireAuthorization();

        endpoints.MapPost(PasskeyPath, async (HttpContext http, IAntiforgery antiforgery, Passkeys passkeys, ITenantContext tenant, AuditTrail audit, Text text) =>
        {
            if (!await ValidAsync(http, antiforgery).ConfigureAwait(false) || http.User.GetUserId() is not { } user)
            {
                return Results.BadRequest();
            }

            string? sealedOptions = http.Request.Cookies[StateCookie];
            http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/" });
            string? remembered = null;
            try
            {
                remembered = sealedOptions is null ? null : Protector(http, user).Unprotect(sealedOptions);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
            }

            (AuthenticatorAssertionRawResponse? response, string? returnUrl) = await ReadAsync(http).ConfigureAwait(false);
            if (remembered is null || response is null)
            {
                return Results.BadRequest(new { error = text["account.passkey.expired_sign_in"] });
            }

            UserId? proved = await passkeys.CompleteSignInAsync(tenant.TenantId, AssertionOptions.FromJson(remembered), response, http.RequestAborted).ConfigureAwait(false);

            // The passkey must be this account's own: confirming as someone else proves nothing.
            if (proved?.Value != user || !await ReissueAsync(http, AuthenticationMethods.Passkey).ConfigureAwait(false))
            {
                audit.Record(AuthAudit.Refused(AuthAudit.StepUp, new UserId(user), "passkey_rejected", AuthenticationMethods.Passkey));
                return Results.BadRequest(new { error = text["account.confirm.passkey_failed"] });
            }

            audit.Record(AuthAudit.Allowed(AuthAudit.StepUp, new UserId(user), AuthenticationMethods.Passkey));

            return Results.Ok(new { redirect = ConsoleServices.LocalReturnPath(returnUrl, http.User) });
        }).RequireAuthorization();

        return endpoints;
    }

    private static ITimeLimitedDataProtector Protector(HttpContext http, Guid user) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("BrightTime.StepUp.v1", user.ToString("N"))
            .ToTimeLimitedDataProtector();

    private static async Task<bool> ValidAsync(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static async Task<(AuthenticatorAssertionRawResponse? Response, string? ReturnUrl)> ReadAsync(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
        {
            return (null, null);
        }

        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            JsonElement root = body.RootElement;
            AuthenticatorAssertionRawResponse? response = root.TryGetProperty("credential", out JsonElement c) && c.ValueKind == JsonValueKind.Object
                ? c.Deserialize<AuthenticatorAssertionRawResponse>()
                : null;
            string? returnUrl = root.TryGetProperty("returnUrl", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return (response, returnUrl);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Turns a failed step-up into "confirm it's you" instead of "access denied": a redirect to the
/// confirm page for a page request, a 403 naming the confirm page for anything else.
/// </summary>
public sealed class StepUpResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        bool stepUp = authorizeResult.Forbidden
                      && authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Handler is RecentAuthenticationHandler) == true;
        if (!stepUp)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        HttpRequest request = context.Request;
        if (HttpMethods.IsGet(request.Method))
        {
            context.Response.Redirect(StepUp.ConfirmPath(request.Path + request.QueryString));
            return;
        }

        // A form post or a script: come back to the page it was sent from.
        string? from = Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out Uri? referer)
                       && referer.Authority == request.Host.Value
            ? referer.PathAndQuery
            : null;

        if (request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = context.RequestServices.GetRequiredService<Text>()["account.confirm.needed"], confirm = StepUp.ConfirmPath(from) }).ConfigureAwait(false);
            return;
        }

        context.Response.Redirect(StepUp.ConfirmPath(from));
    }
}
