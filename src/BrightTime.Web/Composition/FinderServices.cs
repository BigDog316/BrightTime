using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BrightTime.Web.Composition;

/// <summary>Wires the public finder: the clock, the tenant for the request, and the data source.</summary>
public static class FinderServices
{
    public static IServiceCollection AddBrightTimeFinder(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FinderOptions>()
            .Bind(configuration.GetSection(FinderOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Mode != FinderMode.Sql || o.TenantId != Guid.Empty,
                "Finder:TenantId is required in Sql mode until host resolution is wired.")
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
                _ => new SqlListingFinder(sp.GetRequiredService<NpgsqlDataSource>()),
            };
        });

        // The data source exists only in SQL mode, so sample mode never opens a connection.
        FinderOptions bound = configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>() ?? new();
        if (bound.Mode == FinderMode.Sql)
        {
            services.AddSingleton(_ => new NpgsqlDataSourceBuilder(bound.ConnectionString).Build());
        }

        return services;
    }
}
