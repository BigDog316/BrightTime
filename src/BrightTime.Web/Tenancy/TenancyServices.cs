using BrightTime.Modules.Platform.Tenancy;
using BrightTime.Web.Composition;
using Npgsql;

namespace BrightTime.Web.Tenancy;

/// <summary>Wires tenant resolution: the mode, the cached lookup and the per-request site.</summary>
public static class TenancyServices
{
    public static IServiceCollection AddBrightTimeTenancy(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        TenancyOptions tenancy = configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new();
        FinderOptions finder = configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>() ?? new();

        if (finder.Mode == FinderMode.Sql)
        {
            if (tenancy.Mode == TenancyMode.Fixed && tenancy.TenantId == Guid.Empty)
            {
                throw new InvalidOperationException("Tenancy:TenantId is required when Tenancy:Mode is Fixed.");
            }

            if (tenancy.Mode == TenancyMode.Host && tenancy.PublicScheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
            {
                throw new InvalidOperationException("Tenancy:PublicScheme must be https outside Development.");
            }

            services.AddSingleton<SqlTenantLookup>(sp => new SqlTenantLookup(sp.GetRequiredService<NpgsqlDataSource>()));
            services.AddSingleton<ITenantLookup>(sp => new CachedTenantLookup(sp.GetRequiredService<SqlTenantLookup>()));
        }

        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.SectionName));
        services.AddScoped<SiteContext>();
        return services;
    }
}
