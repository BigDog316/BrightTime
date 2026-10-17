using System.Security.Claims;
using System.Threading.RateLimiting;
using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Messaging;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Review;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Composition;
using Fido2NetLib;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace BrightTime.Web.Auth;

/// <summary>Authentication, authorisation and the services behind the provider and organiser consoles.</summary>
public static class ConsoleServices
{
    public const string ReviewPolicy = "console.review";
    public const string VetPolicy = "console.vet";

    private const string AuthDataSourceKey = "auth";

    /// <summary>How long a cookie's claims are trusted before they are rebuilt from the database.</summary>
    public static readonly TimeSpan RevalidateAfter = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddBrightTimeConsoles(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        AuthOptions auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new();
        FinderOptions finder = configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>() ?? new();

        bool hasDatabase = finder.Mode == FinderMode.Sql;
        bool canSignIn = hasDatabase && !string.IsNullOrWhiteSpace(auth.ConnectionString);
        bool devSignIn = canSignIn && auth.DevelopmentSignIn && environment.IsDevelopment();
        services.AddSingleton(new ConsoleAvailability(hasDatabase, canSignIn, devSignIn));

        AuthenticationBuilder authentication = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme);
        authentication
            .AddCookie(options =>
            {
                options.Cookie.Name = "brighttime.session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.LoginPath = "/sign-in";
                options.LogoutPath = "/sign-out";
                options.AccessDeniedPath = "/access-denied";
                options.ReturnUrlParameter = "returnUrl";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnValidatePrincipal = RevalidateAsync;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(ReviewPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(Permissions.Listings.Review)))
            .AddPolicy(VetPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(Permissions.Organisations.Vet)));
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, OrganisationScopeHandler>();

        if (canSignIn)
        {
            if (auth.PublicBaseUrl is not { IsAbsoluteUri: true } baseUrl
                || (baseUrl.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment()))
            {
                throw new InvalidOperationException(
                    "Auth:PublicBaseUrl must be the site's absolute https address when sign-in is enabled; reset links are built from it.");
            }

            services.AddKeyedSingleton(AuthDataSourceKey, (_, _) => NpgsqlDataSource.Create(auth.ConnectionString!));
            services.AddSingleton(sp => new AccountReader(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey)));
            services.AddSingleton(sp => new PasswordAccounts(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
            services.AddScoped<BrightTimePrincipalFactory>();

            // WebAuthn's relying party is the site's own address, as for reset links: the
            // browser checks each passkey against it, so a phishing site cannot use one.
            string origin = baseUrl.GetLeftPart(UriPartial.Authority);
            services.AddSingleton<IFido2>(_ => new Fido2(new Fido2Configuration
            {
                RPID = baseUrl.Host,
                RPName = "BrightTime",
                Origins = new HashSet<string>(StringComparer.Ordinal) { origin },
                TimestampDriftTolerance = 300_000,
            }));
            services.AddSingleton(sp => new ExternalLogins(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
            ExternalSignIn.AddProviders(authentication, auth, environment);
            services.AddSingleton(sp => new Passkeys(
                sp.GetRequiredService<IFido2>(), sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));

            if (auth.CheckBreachedPasswords)
            {
                services.AddHttpClient<IBreachedPasswordCheck, PwnedPasswordsCheck>(c =>
                {
                    c.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
                    c.Timeout = TimeSpan.FromSeconds(3);
                    c.DefaultRequestHeaders.Add("Add-Padding", "true");
                });
            }
            else
            {
                services.AddSingleton<IBreachedPasswordCheck, NoBreachedPasswordCheck>();
            }
        }

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        if (canSignIn)
        {
            services.AddSingleton<PasswordResetDispatcher>();
            services.AddHostedService(sp => sp.GetRequiredService<PasswordResetDispatcher>());
        }

        // T21: every authentication route is rate limited per client address, on top of the
        // per-account lockout, so guessing across many accounts is slowed too.
        int perMinute = Math.Max(1, auth.SignInAttemptsPerMinute);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                HttpMethods.IsPost(http.Request.Method) && IsAuthenticationPath(http.Request.Path)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("unlimited"));
        });

        if (hasDatabase)
        {
            services.AddScoped<ListingEditor>();
            services.AddScoped<OrganisationReview>();
        }

        return services;
    }

    /// <summary>Sign-out always; development sign-in only where <see cref="ConsoleAvailability.DevelopmentSignIn"/> holds.</summary>
    public static IEndpointRouteBuilder MapBrightTimeSignIn(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Both endpoints validate the antiforgery token themselves and read the form after:
        // a bound [FromForm] parameter turns a missing token into a 500 rather than a 400, and
        // an endpoint with no form parameter is not validated by the middleware at all.
        endpoints.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (!await IsValidPostAsync(http, antiforgery).ConfigureAwait(false))
            {
                return Results.BadRequest();
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.LocalRedirect("/");
        });

        if (endpoints.ServiceProvider.GetRequiredService<ConsoleAvailability>().DevelopmentSignIn)
        {
            endpoints.MapPost("/dev/sign-in/as", async (
                HttpContext http,
                IAntiforgery antiforgery,
                ITenantContext tenant,
                BrightTimePrincipalFactory factory) =>
            {
                if (!await IsValidPostAsync(http, antiforgery).ConfigureAwait(false))
                {
                    return Results.BadRequest();
                }

                IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
                if (!Guid.TryParse(form["userId"], out Guid userId))
                {
                    return Results.BadRequest("Choose an account.");
                }

                string? returnUrl = form["returnUrl"];
                ClaimsPrincipal? principal = await factory.CreateAsync(tenant.TenantId, new UserId(userId), http.RequestAborted).ConfigureAwait(false);
                if (principal is null)
                {
                    return Results.BadRequest("That account cannot sign in.");
                }

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
                return Results.LocalRedirect(LocalReturnPath(returnUrl, principal));
            });
        }

        return endpoints;
    }

    private static readonly string[] AuthenticationPaths =
    [
        "/sign-in", "/account/forgot", "/account/reset", "/dev/sign-in/as",
        PasskeyEndpoints.SignInOptionsPath, PasskeyEndpoints.SignInPath,
        PasskeyEndpoints.RegisterOptionsPath, PasskeyEndpoints.RegisterPath,
        ExternalSignIn.StartPath, ExternalSignIn.LinkPath,
    ];

    private static bool IsAuthenticationPath(PathString path) =>
        AuthenticationPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> IsValidPostAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if (!http.Request.HasFormContentType)
        {
            return false;
        }

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

    /// <summary>A local path only. Anything else, including "//host", goes to the person's console.</summary>
    public static string LocalReturnPath(string? returnUrl, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        bool local = returnUrl is { Length: > 0 } url
                     && url[0] == '/'
                     && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
                     && !url.Contains("://", StringComparison.Ordinal);
        if (local)
        {
            return returnUrl!;
        }

        return principal.HasPermission(Permissions.Listings.Review) ? "/organiser/review" : "/provider";
    }

    /// <summary>
    /// Rebuilds the principal from the database once it is a few minutes old, and rejects it
    /// if the account is gone, inactive, re-stamped or from another tenant.
    /// </summary>
    private static async Task RevalidateAsync(CookieValidatePrincipalContext context)
    {
        ClaimsPrincipal? current = context.Principal;
        ITenantContext tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        if (current?.GetTenantId() != tenant.TenantId.Value || current.GetUserId() is not { } userId)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        DateTimeOffset issued = context.Properties.IssuedUtc ?? DateTimeOffset.MinValue;
        DateTimeOffset now = context.HttpContext.RequestServices.GetRequiredService<IClock>().UtcNow;
        if (now - issued < RevalidateAfter)
        {
            return;
        }

        BrightTimePrincipalFactory? factory = context.HttpContext.RequestServices.GetService<BrightTimePrincipalFactory>();
        ClaimsPrincipal? fresh = factory is null
            ? null
            : await factory.CreateAsync(tenant.TenantId, new UserId(userId), context.HttpContext.RequestAborted).ConfigureAwait(false);

        if (fresh is null
            || fresh.FindFirstValue(BrightTimePrincipalFactory.StampClaim) != current.FindFirstValue(BrightTimePrincipalFactory.StampClaim))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        context.ReplacePrincipal(fresh);
        context.ShouldRenew = true;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
