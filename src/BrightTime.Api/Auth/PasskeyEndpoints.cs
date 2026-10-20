using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Fido2NetLib;
using Microsoft.AspNetCore.DataProtection;

namespace BrightTime.Api.Auth;

/// <summary>A ceremony's options, as the platform's passkey API takes them, and the state to send back with its answer.</summary>
public sealed record PasskeyCeremony(JsonNode Options, string State);

public sealed record PasskeyRegistrationRequest(string? State, JsonElement Credential, string? Name);

public sealed record PasskeySignInRequest(string? State, JsonElement Credential, string? DeviceId);

public sealed record PasskeyResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool IsBackedUp);

/// <summary>Which apps may use this site's passkeys (<c>Passkeys:*</c>).</summary>
public sealed class ApiPasskeyOptions
{
    public const string SectionName = "Passkeys";

    /// <summary>
    /// The origins the app's passkey ceremonies come from besides the site's own: on Android,
    /// <c>android:apk-key-hash:&lt;base64url SHA-256 of the signing certificate&gt;</c>. iOS uses the
    /// site's origin, through its associated domain.
    /// </summary>
    public IList<string> AppOrigins { get; } = [];
}

/// <summary>
/// Passkeys from the app (D38; docs/plan/05's <c>/auth/passkeys</c>): add one to a signed-in
/// account, and sign in with one for tokens, as <see cref="TokenEndpoints"/> does with a password.
/// The same <see cref="Passkeys"/> store as the web's, and the same relying party: the region's
/// site, so a passkey made on the web works in the app and the other way round.
/// </summary>
/// <remarks>
/// <para>
/// No cookies here, so the options a ceremony was started with travel back as <c>state</c>:
/// sealed by data protection, bound to their purpose (and for a registration, to the account),
/// and good for five minutes.
/// </para>
/// <para>
/// Adding and removing a passkey changes how the account signs in, so it needs a recent sign-in
/// (T21's step-up), as on the web. Rate limited with the sign-in routes.
/// </para>
/// </remarks>
public static class PasskeyEndpoints
{
    public const string ManagePolicy = "passkeys.manage";
    public static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(5);
    private const string SignInPurpose = "assert";

    public static RouteGroupBuilder MapPasskeyEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder auth = api.MapGroup("/auth/passkeys").RequireRateLimiting(ApiComposition.SignInLimit);
        auth.MapPost("/options", RegistrationOptionsAsync).RequireAuthorization(ManagePolicy);
        auth.MapPost("/", RegisterAsync).RequireAuthorization(ManagePolicy);
        auth.MapPost("/assert/options", SignInOptions).AllowAnonymous();
        auth.MapPost("/assert", SignInAsync).AllowAnonymous();

        RouteGroupBuilder me = api.MapGroup("/me/passkeys").RequireAuthorization();
        me.MapGet("/", ListAsync);
        me.MapDelete("/{id:guid}", RemoveAsync).RequireAuthorization(ManagePolicy);
        return api;
    }

    /// <summary>The relying party for this request's region: its site, and the app's origins.</summary>
    public static IFido2 Fido2For(SiteContext site, ApiPasskeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(options);
        Uri siteUrl = site.BaseUrl ?? throw new InvalidOperationException("Passkeys need the tenant's address, and this request has none.");
        return new Fido2(new Fido2Configuration
        {
            RPID = siteUrl.Host,
            RPName = site.Site?.DisplayName ?? "BrightTime",
            Origins = new HashSet<string>(options.AppOrigins.Prepend(siteUrl.GetLeftPart(UriPartial.Authority)), StringComparer.Ordinal),
            TimestampDriftTolerance = 300_000,
        });
    }

    private static async Task<IResult> RegistrationOptionsAsync(ClaimsPrincipal user, Passkeys passkeys, ITenantContext tenant, IDataProtectionProvider protection, CancellationToken ct)
    {
        UserId me = Actor(user);
        CredentialCreateOptions options = await passkeys.BeginRegistrationAsync(tenant.TenantId, me, user.Identity?.Name ?? "BrightTime account", ct);
        return Results.Ok(new PasskeyCeremony(JsonNode.Parse(options.ToJson())!, Seal(protection, RegisterPurpose(me), options.ToJson())));
    }

    private static async Task<IResult> RegisterAsync(
        PasskeyRegistrationRequest body,
        ClaimsPrincipal user,
        Passkeys passkeys,
        ITenantContext tenant,
        IDataProtectionProvider protection,
        AuditTrail audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        UserId me = Actor(user);
        string? original = Unseal(protection, RegisterPurpose(me), body.State);
        AuthenticatorAttestationRawResponse? response = Read<AuthenticatorAttestationRawResponse>(body.Credential);
        if (original is null || response is null)
        {
            return ApiProblems.From(Error.Validation("passkey.expired", "That request has expired. Start again."));
        }

        Result added = await passkeys.CompleteRegistrationAsync(tenant.TenantId, me, CredentialCreateOptions.FromJson(original), response, body.Name, ct);
        if (!added.IsSuccess)
        {
            return ApiProblems.From(added.Error);
        }

        audit.Record(AuthAudit.Allowed(AuthAudit.PasskeyAdded, me, detail: TokenEndpoints.Channel(null)));
        return Results.Created(new Uri("/api/v1/me/passkeys", UriKind.Relative), null);
    }

    private static IResult SignInOptions(Passkeys passkeys, IDataProtectionProvider protection)
    {
        AssertionOptions options = passkeys.BeginSignIn();
        return Results.Ok(new PasskeyCeremony(JsonNode.Parse(options.ToJson())!, Seal(protection, SignInPurpose, options.ToJson())));
    }

    /// <summary>A passkey for tokens. Every refusal reads as a password's does (T21).</summary>
    private static async Task<IResult> SignInAsync(
        PasskeySignInRequest body,
        Passkeys passkeys,
        BrightTimePrincipalFactory principals,
        TokenSessions sessions,
        AccessTokens tokens,
        TokenOptions options,
        TenantContext tenant,
        SiteContext site,
        IDataProtectionProvider protection,
        AuditTrail audit,
        IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        string? original = Unseal(protection, SignInPurpose, body.State);
        AuthenticatorAssertionRawResponse? response = Read<AuthenticatorAssertionRawResponse>(body.Credential);
        if (original is null || response is null)
        {
            return TokenEndpoints.InvalidGrant();
        }

        DateTimeOffset now = clock.UtcNow;
        UserId? user = await passkeys.CompleteSignInAsync(tenant.TenantId, AssertionOptions.FromJson(original), response, ct);
        ClaimsPrincipal? principal = user is { } id
            ? await principals.CreateAsync(tenant.TenantId, id, new AuthenticationEvent(AuthenticationMethods.Passkey, now), ct)
            : null;
        if (principal is null)
        {
            audit.Record(AuthAudit.Refused(AuthAudit.SignIn, user, "passkey_rejected", AuthenticationMethods.Passkey) with
            { Detail = TokenEndpoints.Channel(AuthenticationMethods.Passkey) });
            return TokenEndpoints.InvalidGrant();
        }

        IssuedRefreshToken refresh = await sessions.IssueAsync(
            tenant.TenantId, user!.Value, AuthenticationMethods.Passkey, body.DeviceId,
            options.LifetimeFor(principal.FindFirstValue(BrightTimeClaims.AccountKind) ?? "adult"), ct);
        audit.Record(AuthAudit.Allowed(AuthAudit.SignIn, user, AuthenticationMethods.Passkey, TokenEndpoints.Channel(AuthenticationMethods.Passkey)));
        return TokenEndpoints.Issued(tokens, principal, site, refresh, now, options);
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, Passkeys passkeys, ITenantContext tenant, CancellationToken ct) =>
        Results.Ok((await passkeys.ListAsync(tenant.TenantId, Actor(user), ct))
            .Select(p => new PasskeyResponse(p.Id, p.Name, p.CreatedAt, p.LastUsedAt, p.IsBackedUp)).ToList());

    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal user, Passkeys passkeys, ITenantContext tenant, AuditTrail audit, CancellationToken ct)
    {
        UserId me = Actor(user);
        if (!await passkeys.RevokeAsync(tenant.TenantId, me, id, ct))
        {
            return ApiProblems.NotFound("You have no passkey with that id.");
        }

        audit.Record(AuthAudit.Allowed(AuthAudit.PasskeyRemoved, me, detail: TokenEndpoints.Channel(null)));
        return Results.NoContent();
    }

    private static string RegisterPurpose(UserId user) => "register:" + user.Value.ToString("N");

    private static string Seal(IDataProtectionProvider protection, string purpose, string json) =>
        Protector(protection, purpose).Protect(json, StateLifetime);

    private static string? Unseal(IDataProtectionProvider protection, string purpose, string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        try
        {
            return Protector(protection, purpose).Unprotect(state);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection, string purpose) =>
        protection.CreateProtector("BrightTime.Api.Passkeys.v1", purpose).ToTimeLimitedDataProtector();

    private static T? Read<T>(JsonElement element) where T : class
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

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
