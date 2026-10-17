using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Jobs;

/// <summary>
/// Wires the worker: module contexts on the jobs login, the cross-module queries the handlers
/// need, the job store and runner, and every handler.
/// </summary>
/// <remarks>
/// The connection must log in as a role that is a member of <c>brighttime_jobs</c>. That role is
/// subject to the tenant policy on every table, so each handler sees only its own job's tenant,
/// except on <c>platform.job</c>, where <c>job_worker_claims</c> lets it claim across tenants.
/// </remarks>
public static class JobsComposition
{
    public const string ConnectionStringName = "Jobs";

    public static IServiceCollection AddBrightTimeWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string connection = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is required: a login that is a member of brighttime_jobs.");

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

        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddSingleton<JobStore>();
        services.Configure<JobRunnerOptions>(configuration.GetSection("Jobs"));
        services.AddSingleton<JobRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
        services.AddSingleton<AuditPartitionMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditPartitionMaintenance>());

        services.AddListingProjection();

        return services;
    }
}
