using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrightTime.Seed;

/// <summary>
/// Projects every seeded listing with the worker's own service, so occurrences and search rows
/// are exactly what the projection job would produce. Running it inline means a finished seed
/// is a searchable site, with no dependency on a worker being up.
/// </summary>
internal static class SeedProjection
{
    public static async Task ProjectAsync(string connection, Guid tenant, IReadOnlyList<Guid> listings, ILogger logger, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

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
        services.AddListingProjection();

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        for (int i = 0; i < listings.Count; i++)
        {
            // A scope per listing, as the job runner does: one change tracker per unit of work.
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            TenantContext context = scope.ServiceProvider.GetRequiredService<TenantContext>();
            context.SetTenant(new TenantId(tenant), TimeZoneInfo.Utc, Locale.Default);
            context.SetActor(UserId.None, ActorKind.Job);

            await scope.ServiceProvider.GetRequiredService<ListingProjectionService>()
                .ProjectAsync(new TenantId(tenant), listings[i], ct).ConfigureAwait(false);

            if ((i + 1) % 100 == 0 || i + 1 == listings.Count)
            {
                SeedLog.Projected(logger, i + 1, listings.Count);
            }
        }
    }
}
