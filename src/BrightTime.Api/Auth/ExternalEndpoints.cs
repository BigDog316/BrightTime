using System.Security.Claims;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Api.Auth;

/// <summary>An external provider the app may offer: its key, for the web's start route, and its name.</summary>
public sealed record ExternalProviderResponse(string Key, string Name);

public sealed record ExternalTokenRequest(string? Code, string? Verifier, string? DeviceId);

/// <summary>
/// External sign-in from the app (D41; docs/plan/05's <c>/auth/external</c>). The provider flow runs
/// on the web, in the system browser, which hands the app a one-time code; here the app trades the
/// code and its PKCE verifier for tokens, as <see cref="TokenEndpoints"/> does a password.
/// </summary>
/// <remarks>
/// The providers are the web's (<c>Auth:External</c>), so a deployment gives both hosts the same
/// section. As on the web (D17), only an account already connected to the provider signs in;
/// nothing is created or matched by email. Rate limited with the sign-in routes.
/// </remarks>
public static class ExternalEndpoints
{
    public static RouteGroupBuilder MapExternalEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder external = api.MapGroup("/auth/external").RequireRateLimiting(ApiComposition.SignInLimit);
        external.MapGet("/providers", Providers).AllowAnonymous();
        external.MapPost("/token", TokenAsync).AllowAnonymous();
        return api;
    }

    private static IResult Providers(IConfiguration configuration) =>
        Results.Ok(configuration.GetSection("Auth:External").GetChildren()
            .Select(p => new ExternalProviderResponse(p.Key, p["DisplayName"] is { Length: > 0 } name ? name : p.Key))
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList());

    private static async Task<IResult> TokenAsync(
        ExternalTokenRequest body,
        AppSignInCodes codes,
        BrightTimePrincipalFactory principals,
        TokenSessions sessions,
        AccessTokens tokens,
        TokenOptions options,
        TenantContext tenant,
        SiteContext site,
        AuditTrail audit,
        IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        DateTimeOffset now = clock.UtcNow;
        (UserId User, string Method)? redeemed = await codes.RedeemAsync(tenant.TenantId, body.Code, body.Verifier, ct);
        ClaimsPrincipal? principal = redeemed is { } r
            ? await principals.CreateAsync(tenant.TenantId, r.User, new AuthenticationEvent(r.Method, now), ct)
            : null;
        if (principal is null)
        {
            audit.Record(AuthAudit.Refused(AuthAudit.SignIn, redeemed?.User, "app_code_rejected", AuthenticationMethods.External) with
            { Detail = TokenEndpoints.Channel(AuthenticationMethods.External) });
            return TokenEndpoints.InvalidGrant();
        }

        (UserId user, string method) = redeemed!.Value;
        IssuedRefreshToken refresh = await sessions.IssueAsync(
            tenant.TenantId, user, method, body.DeviceId, options.LifetimeFor(principal.FindFirstValue(BrightTimeClaims.AccountKind) ?? "adult"), ct);
        audit.Record(AuthAudit.Allowed(AuthAudit.SignIn, user, method, TokenEndpoints.Channel(method)));
        return TokenEndpoints.Issued(tokens, principal, site, refresh, now, options);
    }
}
