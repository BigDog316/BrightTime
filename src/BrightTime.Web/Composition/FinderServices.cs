using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BrightTime.Web.Composition;

/// <summary>Wires the public finder and listing pages: the clock, the tenant for the request, and the data.</summary>
public static class FinderServices
{
    public static IServiceCollection AddBrightTimeFinder(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FinderOptions>()
            .Bind(configuration.GetSection(FinderOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Mode != FinderMode.Sql || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Finder:ConnectionString is required in Sql mode.")
            .ValidateOnStart();

        services.AddSingleton<IClock>(SystemClock.Instance);

        // The tenant context is per request. The middleware writes it; the page reads it.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddSingleton<IListingFinder>(sp =>
        {
            FinderOptions options = sp.GetRequiredService<IOptions<FinderOptions>>().Value;
            IClock clock = sp.GetRequiredService<IClock>();

            return options.Mode switch
            {
                FinderMode.Sample => new InMemoryListingFinder(KettleCatalogue.Rows(clock), clock),
                _ => new SqlListingFinder(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IClock>()),
            };
        });

        // The database exists only in SQL mode, so sample mode never opens a connection.
        FinderOptions bound = configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>() ?? new();
        if (bound.Mode == FinderMode.Sql)
        {
            string connection = bound.ConnectionString!;
            services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connection).Build());

            // The listing page reads through the module contexts, under the tenant's RLS as
            // brighttime_app; only the search itself steps into brighttime_finder (D13).
            services.AddBrightTimeDbContext<OpportunitiesDbContext>(
                connection, OpportunitiesDbContext.SchemaName, OpportunitiesDbContext.MigrationsHistoryTable);
            services.AddBrightTimeDbContext<DirectoryDbContext>(
                connection, DirectoryDbContext.SchemaName, DirectoryDbContext.MigrationsHistoryTable);
            services.AddBrightTimeDbContext<PlatformDbContext>(
                connection, PlatformDbContext.SchemaName, PlatformDbContext.MigrationsHistoryTable);

            services.AddScoped<IDirectoryQueries, DirectoryQueries>();
            services.AddScoped<PlatformQueries>();
            services.AddScoped<ITaxonomyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
            services.AddScoped<IGeographyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
            services.AddScoped<IFeatureFlags>(sp => sp.GetRequiredService<PlatformQueries>());
            services.AddScoped<IListingDetails, SqlListingDetails>();
            services.AddScoped<ICurationQueries, Modules.Opportunities.Curation.CurationQueries>();
            services.AddScoped<IOrganisationProfiles, OrganisationProfiles>();
        }
        else
        {
            services.AddSingleton<ICurationQueries, NoCuration>();
            services.AddSingleton<IOrganisationProfiles, NoOrganisationProfiles>();
            services.AddSingleton<IGeographyQueries, NoGeography>();
            services.AddSingleton<IListingDetails>(sp =>
            {
                IClock clock = sp.GetRequiredService<IClock>();
                return new InMemoryListingDetails(KettleCatalogue.Details(clock), clock);
            });
        }

        return services;
    }
}
