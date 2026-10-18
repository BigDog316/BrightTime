using System.Security.Claims;
using System.Text.Json;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Localisation;
using Fido2NetLib;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace BrightTime.Web.Auth;

/// <summary>
/// The four WebAuthn round trips: begin and finish registering a passkey, begin and finish
/// signing in with one. JSON, called by <c>wwwroot/passkeys.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// The options issued by "begin" (they carry the challenge) are held in a short-lived,
/// encrypted, HttpOnly cookie rather than in server memory, so any instance can finish what
/// another began. Registration options are bound to the account that asked for them.
/// </para>
/// <para>
/// Every call needs the antiforgery token in a request header, and all four are rate limited
/// with the other sign-in routes.
/// </para>
/// </remarks>
public static class PasskeyEndpoints
{
    public const string RegisterOptionsPath = "/account/passkeys/options";
    public const string RegisterPath = "/account/passkeys";
    public const string SignInOptionsPath = "/sign-in/passkey/options";
    public const string SignInPath = "/sign-in/passkey";

    private const string StateCookie = "brighttime.webauthn";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapBrightTimePasskeys(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(RegisterOptionsPath, async (HttpContext http, IAntiforgery antiforgery, Passkeys passkeys, ITenantContext tenant) =>
        {
            if (!await ValidAsync(http, antiforgery).ConfigureAwait(false) || http.User.GetUserId() is not { } user)
            {
                return Results.BadRequest();
            }

            CredentialCreateOptions options = await passkeys.BeginRegistrationAsync(
                tenant.TenantId, new UserId(user), http.User.Identity?.Name ?? "BrightTime account", http.RequestAborted).ConfigureAwait(false);
            Remember(http, RegisterPurpose(user), options.ToJson());
            return Results.Content(options.ToJson(), "application/json");
        }).RequireAuthorization(StepUp.Policy);

        endpoints.MapPost(RegisterPath, async (HttpContext http, IAntiforgery antiforgery, Passkeys passkeys, ITenantContext tenant, AuditTrail audit, Text text) =>
        {
            if (!await ValidAsync(http, antiforgery).ConfigureAwait(false) || http.User.GetUserId() is not { } user)
            {
                return Results.BadRequest();
            }

            string? remembered = Recall(http, RegisterPurpose(user));
            (JsonElement credential, string? name, _) = await ReadBodyAsync(http).ConfigureAwait(false);
            AuthenticatorAttestationRawResponse? response = Deserialize<AuthenticatorAttestationRawResponse>(credential);
            if (remembered is null || response is null)
            {
                return Problem(text["account.passkey.expired_add"]);
            }

            Result result = await passkeys.CompleteRegistrationAsync(
                tenant.TenantId, new UserId(user), CredentialCreateOptions.FromJson(remembered), response, name, http.RequestAborted).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Problem(text.Error(result.Error!));
            }

            audit.Record(AuthAudit.Allowed(AuthAudit.PasskeyAdded, new UserId(user)));
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(StepUp.Policy);

        endpoints.MapPost(SignInOptionsPath, async (HttpContext http, IAntiforgery antiforgery, Passkeys passkeys) =>
        {
            if (!await ValidAsync(http, antiforgery).ConfigureAwait(false))
            {
                return Results.BadRequest();
            }

            AssertionOptions options = passkeys.BeginSignIn();
            Remember(http, SignInPurpose, options.ToJson());
            return Results.Content(options.ToJson(), "application/json");
        });

        endpoints.MapPost(SignInPath, async (
            HttpContext http,
            IAntiforgery antiforgery,
            Passkeys passkeys,
            ITenantContext tenant,
            BrightTimePrincipalFactory factory,
            IClock clock,
            AuditTrail audit,
            Text text) =>
        {
            if (!await ValidAsync(http, antiforgery).ConfigureAwait(false))
            {
                return Results.BadRequest();
            }

            string? remembered = Recall(http, SignInPurpose);
            (JsonElement credential, _, string? returnUrl) = await ReadBodyAsync(http).ConfigureAwait(false);
            AuthenticatorAssertionRawResponse? response = Deserialize<AuthenticatorAssertionRawResponse>(credential);
            if (remembered is null || response is null)
            {
                return Problem(text["account.passkey.expired_sign_in"]);
            }

            UserId? user = await passkeys.CompleteSignInAsync(
                tenant.TenantId, AssertionOptions.FromJson(remembered), response, http.RequestAborted).ConfigureAwait(false);
            ClaimsPrincipal? principal = user is { } id
                ? await factory.CreateAsync(tenant.TenantId, id, new AuthenticationEvent(AuthenticationMethods.Passkey, clock.UtcNow), http.RequestAborted).ConfigureAwait(false)
                : null;
            if (principal is null)
            {
                audit.Record(AuthAudit.Refused(AuthAudit.SignIn, user, "passkey_rejected", AuthenticationMethods.Passkey));
                return Problem(text["account.passkey.sign_in_failed"]);
            }

            audit.Record(AuthAudit.Allowed(AuthAudit.SignIn, user, AuthenticationMethods.Passkey));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
            return Results.Ok(new { redirect = ConsoleServices.LocalReturnPath(returnUrl, principal) });
        });

        return endpoints;
    }

    private const string SignInPurpose = "assert";

    private static string RegisterPurpose(Guid user) => "register:" + user.ToString("N");

    private static IResult Problem(string message) => Results.BadRequest(new { error = message });

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

    /// <summary>Holds the issued options in an encrypted cookie for a few minutes, bound to its purpose.</summary>
    private static void Remember(HttpContext http, string purpose, string json)
    {
        string sealedValue = Protector(http, purpose).Protect(json, StateLifetime);
        http.Response.Cookies.Append(StateCookie, sealedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            MaxAge = StateLifetime,
            Path = "/",
        });
    }

    /// <summary>The options this browser was issued for this purpose, once; the cookie is cleared either way.</summary>
    private static string? Recall(HttpContext http, string purpose)
    {
        string? value = http.Request.Cookies[StateCookie];
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/" });
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Protector(http, purpose).Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static ITimeLimitedDataProtector Protector(HttpContext http, string purpose) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("BrightTime.Passkeys.v1", purpose)
            .ToTimeLimitedDataProtector();

    private static async Task<(JsonElement Credential, string? Name, string? ReturnUrl)> ReadBodyAsync(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
        {
            return (default, null, null);
        }

        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            JsonElement root = body.RootElement;
            return (
                root.TryGetProperty("credential", out JsonElement c) ? c.Clone() : default,
                root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                root.TryGetProperty("returnUrl", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null);
        }
        catch (JsonException)
        {
            return (default, null, null);
        }
    }

    private static T? Deserialize<T>(JsonElement element) where T : class
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
