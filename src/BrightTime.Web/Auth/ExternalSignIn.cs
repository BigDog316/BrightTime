using System.Security.Claims;
using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace BrightTime.Web.Auth;

/// <summary>
/// Sign-in with external OpenID Connect providers (decision D17).
/// </summary>
/// <remarks>
/// <para>
/// Each provider is an authorization-code flow with PKCE. ASP.NET Core's handler checks the
/// state, the nonce and the ID token's signature, issuer, audience and lifetime, then signs in
/// to a short-lived "external" cookie. <see cref="FinishPath"/> reads that cookie once, throws
/// it away, and does exactly one of two things:
/// </para>
/// <list type="bullet">
/// <item>signs in the account already connected to that issuer and subject; or</item>
/// <item>when the flow was started from Sign-in and security, connects the external account
/// to the person who started it, if they are still the one signed in.</item>
/// </list>
/// <para>
/// It never creates an account and never matches on email.
/// </para>
/// </remarks>
public static partial class ExternalSignIn
{
    public const string ExternalScheme = "BrightTime.External";
    public const string StartPath = "/sign-in/external";
    public const string LinkPath = "/account/external/link";
    public const string FinishPath = "/sign-in/external/done";

    private const string ProviderItem = "brighttime.provider";
    private const string LinkItem = "brighttime.link";
    private const string ReturnItem = "brighttime.return";

    public static string SchemeFor(string provider) => "oidc-" + provider;

    public static void AddProviders(AuthenticationBuilder builder, AuthOptions auth, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(auth);

        CookieSecurePolicy secure = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        builder.AddCookie(ExternalScheme, o =>
        {
            o.Cookie.Name = "brighttime.external";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = secure;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            o.SlidingExpiration = false;
        });

        foreach ((string key, ExternalProviderOptions provider) in auth.External)
        {
            if (!ProviderKey().IsMatch(key) || provider.Authority is null || string.IsNullOrWhiteSpace(provider.ClientId))
            {
                throw new InvalidOperationException(
                    $"Auth:External:{key} needs a name of a-z, 0-9 and hyphens, an Authority and a ClientId.");
            }

            builder.AddOpenIdConnect(SchemeFor(key), provider.DisplayName, o =>
            {
                o.SignInScheme = ExternalScheme;
                o.Authority = provider.Authority.AbsoluteUri;
                o.ClientId = provider.ClientId;
                o.ClientSecret = provider.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;

                // Query, not form_post: the provider's redirect is then a top-level GET, so the
                // correlation and nonce cookies can stay SameSite=Lax rather than None.
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.UsePkce = true;
                o.CallbackPath = "/signin-oidc-" + key;
                o.Scope.Clear();
                o.Scope.Add(OpenIdConnectScope.OpenId);
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.CorrelationCookie.SecurePolicy = secure;
                o.NonceCookie.SameSite = SameSiteMode.Lax;
                o.NonceCookie.SecurePolicy = secure;
                o.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect("/sign-in?external=failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
                o.Events.OnAccessDenied = context =>
                {
                    context.Response.Redirect("/sign-in?external=cancelled");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }
    }

    public static IEndpointRouteBuilder MapExternalSignIn(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Start: a form post with antiforgery, so another site cannot start a flow for you.
        endpoints.MapPost(StartPath, async (HttpContext http, IAntiforgery antiforgery, IOptions<AuthOptions> options) =>
        {
            IFormCollection? form = await ValidFormAsync(http, antiforgery).ConfigureAwait(false);
            string provider = form?["provider"].ToString() ?? string.Empty;
            if (form is null || !options.Value.External.ContainsKey(provider))
            {
                return Results.BadRequest();
            }

            return Challenge(provider, link: null, returnUrl: form["returnUrl"]);
        });

        endpoints.MapPost(LinkPath, async (HttpContext http, IAntiforgery antiforgery, IOptions<AuthOptions> options) =>
        {
            IFormCollection? form = await ValidFormAsync(http, antiforgery).ConfigureAwait(false);
            string provider = form?["provider"].ToString() ?? string.Empty;
            if (form is null || !options.Value.External.ContainsKey(provider) || http.User.GetUserId() is not { } user)
            {
                return Results.BadRequest();
            }

            return Challenge(provider, link: user, returnUrl: null);
        }).RequireAuthorization(StepUp.Policy);

        endpoints.MapGet(FinishPath, async (
            HttpContext http,
            ITenantContext tenant,
            IOptions<AuthOptions> options,
            ExternalLogins logins,
            BrightTimePrincipalFactory factory,
            IClock clock,
            AuditTrail audit) =>
        {
            AuthenticateResult external = await http.AuthenticateAsync(ExternalScheme).ConfigureAwait(false);
            await http.SignOutAsync(ExternalScheme).ConfigureAwait(false);

            if (!external.Succeeded
                || external.Properties?.Items.TryGetValue(ProviderItem, out string? provider) != true
                || provider is null
                || !options.Value.External.TryGetValue(provider, out ExternalProviderOptions? settings)
                || external.Principal.FindFirst("sub") is not { Value.Length: > 0 } sub)
            {
                audit.Record(AuthAudit.Refused(AuthAudit.SignIn, null, "external_flow_failed", AuthenticationMethods.External));
                return Results.LocalRedirect("/sign-in?external=failed");
            }

            // The issuer the handler validated the token against, not anything the token body says.
            string issuer = sub.Issuer;
            IDictionary<string, string?> items = external.Properties.Items;

            if (items.TryGetValue(LinkItem, out string? linkFor) && linkFor is not null)
            {
                // Connect only for the person who started it, and only while they are still signed in.
                if (http.User.GetUserId() is not { } current || current.ToString("N") != linkFor)
                {
                    return Results.LocalRedirect("/account/security?external=wrong_session");
                }

                Result linked = await logins.LinkAsync(tenant.TenantId, new UserId(current), issuer, sub.Value, settings.DisplayName, http.RequestAborted).ConfigureAwait(false);
                audit.Record(linked.IsSuccess
                    ? AuthAudit.Allowed(AuthAudit.ExternalConnected, new UserId(current), detail: new Dictionary<string, string> { ["provider"] = provider })
                    : AuthAudit.Refused(AuthAudit.ExternalConnected, new UserId(current), linked.Error!.Code));
                return Results.LocalRedirect(linked.IsSuccess
                    ? "/account/security?external=connected"
                    : "/account/security?external=" + (linked.Error!.Code == "external.in_use" ? "in_use" : "already"));
            }

            UserId? user = await logins.FindAsync(tenant.TenantId, issuer, sub.Value, http.RequestAborted).ConfigureAwait(false);
            ClaimsPrincipal? principal = user is { } id
                ? await factory.CreateAsync(tenant.TenantId, id, new AuthenticationEvent(AuthenticationMethods.External, clock.UtcNow), http.RequestAborted).ConfigureAwait(false)
                : null;
            if (principal is null)
            {
                audit.Record(AuthAudit.Refused(AuthAudit.SignIn, user, "external_account_not_connected", AuthenticationMethods.External));
                return Results.LocalRedirect("/sign-in?external=unconnected&provider=" + Uri.EscapeDataString(provider));
            }

            audit.Record(AuthAudit.Allowed(AuthAudit.SignIn, user, AuthenticationMethods.External, new Dictionary<string, string> { ["provider"] = provider }));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
            items.TryGetValue(ReturnItem, out string? returnUrl);
            return Results.LocalRedirect(ConsoleServices.LocalReturnPath(returnUrl, principal));
        });

        return endpoints;
    }

    private static IResult Challenge(string provider, Guid? link, string? returnUrl)
    {
        var properties = new AuthenticationProperties { RedirectUri = FinishPath };
        properties.Items[ProviderItem] = provider;
        if (link is { } user)
        {
            properties.Items[LinkItem] = user.ToString("N");
        }

        if (!string.IsNullOrEmpty(returnUrl))
        {
            properties.Items[ReturnItem] = returnUrl;
        }

        return Results.Challenge(properties, [SchemeFor(provider)]);
    }

    private static async Task<IFormCollection?> ValidFormAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if (!http.Request.HasFormContentType)
        {
            return null;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return null;
        }

        return await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex ProviderKey();
}
