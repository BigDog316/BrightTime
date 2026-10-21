using System.Threading.RateLimiting;
using BrightTime.Api.Auth;
using BrightTime.Api.Consent;
using BrightTime.Api.Public;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Health;
using BrightTime.Hosting.Localisation;
using BrightTime.Hosting.Mail;
using BrightTime.Hosting.Security;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Modules.Notifications;
using BrightTime.Modules.Notifications.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Curation;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Personal;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace BrightTime.Api;

/// <summary>
/// Wires the API host: two logins (the application's and the authentication path's), tenant
/// resolution by host, bearer tokens, the audit trail, account and consent mail, rate limits
/// and health.
/// </summary>
/// <remarks>
/// <c>ConnectionStrings:App</c> logs in as a member of <c>brighttime_app</c>, under the tenant
/// policy and the actor guard. <c>ConnectionStrings:Auth</c> logs in as a member of
/// <c>brighttime_auth</c>, which reads the Identity stores and nothing else; only sign-in,
/// tokens and the principal use it.
/// </remarks>
public static class ApiComposition
{
    public const string SignInLimit = "sign-in";
    public const string AuthDataSourceKey = "auth";

    public static IServiceCollection AddBrightTimeApi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        string app = configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is required: a login that is a member of brighttime_app.");
        string auth = configuration.GetConnectionString("Auth")
            ?? throw new InvalidOperationException("ConnectionStrings:Auth is required: a login that is a member of brighttime_auth.");

        TenancyOptions tenancy = configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new();
        if (tenancy.Mode == TenancyMode.Host && tenancy.PublicScheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Tenancy:PublicScheme must be https outside Development: token issuers are tenants' addresses.");
        }

        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        // The application's login, for module contexts, the audit trail, tenant lookup and health.
        services.AddSingleton(_ => NpgsqlDataSource.Create(app));
        services.AddBrightTimeDbContext<DirectoryDbContext>(app, DirectoryDbContext.SchemaName, DirectoryDbContext.MigrationsHistoryTable);
        services.AddScoped<IDirectoryQueries, DirectoryQueries>();

        // The public read surface: the same services as the web's finder pages. Module contexts
        // read under the tenant's RLS as brighttime_app; only the search steps into
        // brighttime_finder (D13).
        services.AddBrightTimeDbContext<OpportunitiesDbContext>(app, OpportunitiesDbContext.SchemaName, OpportunitiesDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<PlatformDbContext>(app, PlatformDbContext.SchemaName, PlatformDbContext.MigrationsHistoryTable);
        services.AddScoped<PlatformQueries>();
        services.AddScoped<ITaxonomyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddScoped<IGeographyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddSingleton<IListingFinder>(sp => new SqlListingFinder(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IListingDetails, SqlListingDetails>();
        services.AddScoped<ICurationQueries, CurationQueries>();
        services.AddScoped<IOrganisationProfiles, OrganisationProfiles>();
        services.AddScoped<PersonalFinder>();
        services.AddScoped<ListingReminders>();

        // Guardianship and consent: the identity context as brighttime_app, under the tenant's RLS
        // and the actor guard; who is whose guardian is decided by Guardianship itself (D4).
        services.AddBrightTimeDbContext<IdentityDbContext>(app, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
        services.AddScoped<IIdentityQueries, IdentityQueries>();
        services.AddScoped<Guardianship>();
        services.AddScoped<AccountClaims>();
        services.AddScoped<AccountTerms>();

        // Notifications: devices, preferences and the inbox. Push tokens are encrypted under the
        // tenant's data key (D32).
        services.AddBrightTimeDbContext<NotificationsDbContext>(app, NotificationsDbContext.SchemaName, NotificationsDbContext.MigrationsHistoryTable);
        services.AddFieldEncryption(configuration, environment.IsDevelopment());
        services.AddNotifications();

        // Enrollment (M3a): the family profile, owned by its account and encrypted under the
        // tenant's data key (D32, D43).
        services.AddBrightTimeDbContext<EnrollmentDbContext>(app, EnrollmentDbContext.SchemaName, EnrollmentDbContext.MigrationsHistoryTable);
        services.AddEnrollment();

        // The authentication path's login: Identity stores only.
        services.AddKeyedSingleton(AuthDataSourceKey, (_, _) => NpgsqlDataSource.Create(auth));
        services.AddSingleton(sp => new AccountReader(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey)));
        services.AddSingleton(sp => new PasswordAccounts(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new TokenSessions(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new AccountProvisioning(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey)));
        services.AddSingleton(sp => new AppSignInCodes(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
        services.AddScoped<BrightTimePrincipalFactory>();

        // New passwords set from the app (a reset, a claim) are checked against Have I Been Pwned's
        // list by hash prefix, as the web's are, when Auth:CheckBreachedPasswords is on.
        if (configuration.GetValue<bool>("Auth:CheckBreachedPasswords"))
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

        // Passkeys (D38): the web's store, with this request's region as the relying party and
        // the app's origins besides; the options a ceremony started with are sealed by data
        // protection, whose key ring every instance shares (D39).
        services.Configure<ApiPasskeyOptions>(configuration.GetSection(ApiPasskeyOptions.SectionName));
        services.AddScoped(sp => PasskeyEndpoints.Fido2For(sp.GetRequiredService<SiteContext>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiPasskeyOptions>>().Value));
        services.AddScoped(sp => new Passkeys(sp.GetRequiredService<Fido2NetLib.IFido2>(), sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
        services.AddBrightTimeDataProtection(configuration, "BrightTime.Api", environment.IsDevelopment(), hasDatabase: true);

        // Account and consent mail (Email:*), composed from the shared catalogue with the
        // tenant's wording, and sent after the response.
        services.AddBrightTimeText(hasDatabase: true);
        services.AddBrightTimeEmail(configuration);
        services.AddAccountMail();

        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.SectionName));
        services.AddSingleton(sp => new SqlTenantLookup(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ITenantLookup>(sp => new CachedTenantLookup(sp.GetRequiredService<SqlTenantLookup>()));
        services.AddScoped<SiteContext>();

        services.AddClientNetwork(configuration);
        services.AddBrightTimeAudit(configuration, environment, hasDatabase: true);
        services.AddBrightTimeHealth(configuration, hasDatabase: true);
        services.AddProblemDetails();
        services.AddOpenApi();

        AddTokens(services, configuration, environment);

        int publicPerMinute = Math.Max(1, configuration.GetValue("Api:PublicRequestsPerMinute", 300));
        int perMinute = Math.Max(1, configuration.GetValue("Auth:SignInAttemptsPerMinute", 10));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (rejected, ct) =>
            {
                rejected.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(PublicEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientNetwork.PartitionKey(http.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = publicPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(SignInLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientNetwork.PartitionKey(http.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return services;
    }

    private static void AddTokens(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        TokenOptions options = configuration.GetSection(TokenOptions.SectionName).Get<TokenOptions>() ?? new();
        var tokens = new AccessTokens(options, options.Keys(environment.IsDevelopment()));
        services.AddSingleton(options);
        services.AddSingleton(tokens);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IHttpContextAccessor>((o, accessor) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    // The issuer is the request's own tenant's address: a token from another
                    // tenant is refused before its tenant claim is even read.
                    ValidateIssuer = true,
                    IssuerValidator = (issuer, _, _) =>
                        accessor.HttpContext?.RequestServices.GetRequiredService<SiteContext>().BaseUrl?.AbsoluteUri == issuer
                            ? issuer
                            : throw new SecurityTokenInvalidIssuerException("The token was issued by another site."),
                    ValidAudience = TokenOptions.Audience,
                    IssuerSigningKeys = tokens.VerificationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                o.Events = new JwtBearerEvents { OnTokenValidated = BearerPrincipal.ValidateAsync };
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(ConsentEndpoints.AttestPolicy, p => p.RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(Permissions.Consent.AttestSignedForm)))
            .AddPolicy(PasskeyEndpoints.ManagePolicy, p => p.RequireAuthenticatedUser()
                .AddRequirements(new RecentAuthenticationRequirement(PasskeyEndpoints.StepUpWindow)));
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, RecentAuthenticationHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, OrganisationScopeHandler>();
    }
}
