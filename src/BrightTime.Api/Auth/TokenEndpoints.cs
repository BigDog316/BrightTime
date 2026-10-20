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

/// <summary>Sign in with a password; device names are the client's own label, for the sessions list later.</summary>
public sealed record TokenRequest(string? Email, string? Password, string? DeviceId);

public sealed record RefreshRequest(string? RefreshToken);

/// <summary>An access token and the refresh token that renews it. The refresh token is shown once.</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// The mobile app's sign-in (docs/plan/05, "Identity and consent"): password for tokens, refresh
/// with rotation, sign out one session or every one.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal reads the same, whatever the reason (no such account, wrong password, locked,
/// not allowed to sign in), after the same password-hashing work (T21). Sign-ins and refusals
/// are audited as the web's are; a routine refresh is not, a reused token is.
/// </para>
/// <para>Rate limited per client address with the sign-in routes.</para>
/// </remarks>
public static class TokenEndpoints
{
    public const string InvalidGrantType = "urn:brighttime:problem:invalid-grant";

    public static RouteGroupBuilder MapTokenEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder auth = api.MapGroup("/auth").RequireRateLimiting(ApiComposition.SignInLimit);
        auth.MapPost("/token", TokenAsync).AllowAnonymous();
        auth.MapPost("/token/refresh", RefreshAsync).AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous();
        return api;
    }

    private static async Task<IResult> TokenAsync(
        TokenRequest request,
        PasswordAccounts passwords,
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
        string email = request.Email?.Trim() ?? string.Empty;
        string password = request.Password ?? string.Empty;
        if (email.Length is 0 or > 320 || password.Length is 0 or > 1024)
        {
            return InvalidGrant();
        }

        PasswordCheckOutcome outcome = await passwords.CheckPasswordWithOutcomeAsync(tenant.TenantId, email, password, ct);
        DateTimeOffset now = clock.UtcNow;
        ClaimsPrincipal? principal = outcome.SignedIn is { } signedIn
            ? await principals.CreateAsync(tenant.TenantId, signedIn, new AuthenticationEvent(AuthenticationMethods.Password, now), ct)
            : null;
        if (principal is null)
        {
            audit.Record(AuthAudit.Refused(AuthAudit.SignIn, outcome.Account, outcome.Failure ?? PasswordCheckOutcome.NotEligible, AuthenticationMethods.Password)
                with
            { Detail = Channel(AuthenticationMethods.Password) });
            return InvalidGrant();
        }

        UserId user = outcome.SignedIn!.Value;
        IssuedRefreshToken refresh = await sessions.IssueAsync(
            tenant.TenantId, user, AuthenticationMethods.Password, request.DeviceId, options.LifetimeFor(principal.FindFirstValue(BrightTimeClaims.AccountKind) ?? "adult"), ct);
        audit.Record(AuthAudit.Allowed(AuthAudit.SignIn, user, AuthenticationMethods.Password, Channel(AuthenticationMethods.Password)));
        return Issued(tokens, principal, site, refresh, now, options);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
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
        RefreshOutcome outcome = await sessions.RotateAsync(tenant.TenantId, request.RefreshToken ?? string.Empty, options.LifetimeFor, ct);
        switch (outcome)
        {
            case RefreshOutcome.Rotated rotated:
                ClaimsPrincipal? principal = await principals.CreateAsync(
                    tenant.TenantId, rotated.User, new AuthenticationEvent(rotated.AuthMethod, rotated.AuthenticatedAt), ct);
                if (principal is null)
                {
                    // The account changed under the session (suspended, say): end it.
                    await sessions.SignOutAsync(tenant.TenantId, rotated.Next.Token, ct);
                    return InvalidGrant();
                }

                return Issued(tokens, principal, site, rotated.Next, clock.UtcNow, options);

            case RefreshOutcome.Reused reused:
                audit.Record(AuthAudit.Refused(AuthAudit.TokenReused, reused.User,
                    "A spent refresh token was presented again; every session in its family was ended.") with
                { Detail = Channel(null) });
                return InvalidGrant();

            default:
                return InvalidGrant();
        }
    }

    /// <summary>Ends the session the refresh token belongs to. 204 whether or not it was one, so the answer says nothing.</summary>
    private static async Task<IResult> LogoutAsync(RefreshRequest request, TokenSessions sessions, TenantContext tenant, AuditTrail audit, CancellationToken ct)
    {
        if (await sessions.SignOutAsync(tenant.TenantId, request.RefreshToken ?? string.Empty, ct) is { } user)
        {
            audit.Record(AuthAudit.Allowed(AuthAudit.SignOut, user, detail: Channel(null)));
        }

        return Results.NoContent();
    }

    internal static IResult Issued(AccessTokens tokens, ClaimsPrincipal principal, SiteContext site, IssuedRefreshToken refresh, DateTimeOffset now, TokenOptions options)
    {
        Uri issuer = site.BaseUrl ?? throw new InvalidOperationException("The API issues tokens only for a tenant with an address.");
        (string access, _) = tokens.Issue(principal, issuer, now);
        return Results.Ok(new TokenResponse(access, "Bearer", (int)options.AccessLifetime.TotalSeconds, refresh.Token, refresh.ExpiresAt));
    }

    /// <summary>The one answer every refused grant gets.</summary>
    internal static IResult InvalidGrant() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        type: InvalidGrantType,
        title: "Those details did not work. Check them and try again, or reset your password.");

    internal static Dictionary<string, string> Channel(string? method)
    {
        Dictionary<string, string> detail = new(StringComparer.Ordinal) { ["channel"] = "api" };
        if (method is not null)
        {
            detail["method"] = method;
        }

        return detail;
    }
}
