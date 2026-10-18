using System.Threading.RateLimiting;
using BrightTime.Api.Auth;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Health;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace BrightTime.Api;

/// <summary>
/// Wires the API host: two logins (the application's and the authentication path's), tenant
/// resolution by host, bearer tokens, the audit trail, rate limits and health.
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

        // The authentication path's login: Identity stores only.
        services.AddKeyedSingleton(AuthDataSourceKey, (_, _) => NpgsqlDataSource.Create(auth));
        services.AddSingleton(sp => new AccountReader(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey)));
        services.AddSingleton(sp => new PasswordAccounts(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new TokenSessions(sp.GetRequiredKeyedService<NpgsqlDataSource>(AuthDataSourceKey), sp.GetRequiredService<IClock>()));
        services.AddScoped<BrightTimePrincipalFactory>();

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

        int perMinute = Math.Max(1, configuration.GetValue("Auth:SignInAttemptsPerMinute", 10));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (rejected, ct) =>
            {
                rejected.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
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
        services.AddAuthorization();
    }
}
