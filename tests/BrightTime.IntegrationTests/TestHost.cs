using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Outbox;
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
using Microsoft.Extensions.Options;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// Builds service providers the way the hosts will: module contexts with their interceptors,
/// connecting as a login role subject to row-level security, never as the superuser.
/// </summary>
internal static class TestHost
{
    public const string AppLogin = "bt_app_login";
    public const string JobsLogin = "bt_jobs_login";
    public const string FinderLogin = "bt_finder_login";
    public const string Password = "bt-test-only";

    public static string As(string superuserConnection, string login) =>
        new NpgsqlConnectionStringBuilder(superuserConnection) { Username = login, Password = Password }.ConnectionString;

    /// <summary>Creates the three test logins if absent, with the memberships production grants.</summary>
    public static async Task EnsureLoginsAsync(string superuserConnection, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(superuserConnection, ct);
        await Sql.ExecAsync(connection, null,
            """
            do $$ begin
              if not exists (select from pg_roles where rolname = 'bt_app_login') then
                create role bt_app_login login password 'bt-test-only' in role brighttime_app;
              end if;
              if not exists (select from pg_roles where rolname = 'bt_jobs_login') then
                create role bt_jobs_login login password 'bt-test-only' in role brighttime_jobs;
              end if;
              if not exists (select from pg_roles where rolname = 'bt_finder_login') then
                create role bt_finder_login login password 'bt-test-only' in role brighttime_app;
              end if;
              grant brighttime_finder to bt_app_login;
              grant brighttime_finder to bt_finder_login;
            end $$;
            """, []);
    }

    /// <summary>A request-style scope for the application login, with the tenant set.</summary>
    public static AsyncServiceScope AppScope(string superuserConnection, Guid tenant)
    {
        ServiceProvider provider = ModuleServices(As(superuserConnection, AppLogin)).BuildServiceProvider();
        AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(tenant), TimeZoneInfo.Utc, Locale.English);
        return scope;
    }

    /// <summary>
    /// Dispatches the outbox and runs due jobs until neither has anything left, as the jobs host
    /// does continuously: a delivery can queue a projection, which the next round runs.
    /// </summary>
    public static async Task DrainAsync(ServiceProvider worker, CancellationToken ct)
    {
        OutboxDispatcher outbox = worker.GetRequiredService<OutboxDispatcher>();
        JobRunner runner = worker.GetRequiredService<JobRunner>();
        for (int round = 0; round < 20; round++)
        {
            int dispatched = await outbox.DrainAsync(ct);
            int ran = await runner.DrainAsync(ct);
            if (dispatched == 0 && ran == 0)
            {
                return;
            }
        }
    }

    /// <summary>The worker, as the jobs host builds it, on its own queue.</summary>
    public static ServiceProvider Worker(
        string superuserConnection,
        string queue,
        Action<IServiceCollection>? extra = null)
    {
        string jobs = As(superuserConnection, JobsLogin);
        ServiceCollection services = ModuleServices(jobs);

        // Created by the container, so disposing the provider disposes the data source.
        services.AddSingleton(_ => NpgsqlDataSource.Create(jobs));
        services.AddSingleton<JobStore>();
        services.AddSingleton(Options.Create(new JobRunnerOptions
        {
            Queue = queue,
            WorkerId = "test-worker",
            LockDuration = TimeSpan.FromMinutes(5),
        }));
        services.AddSingleton<JobRunner>();
        services.AddOutboxDispatch(runInBackground: false);
        services.AddListingProjection();
        extra?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static ServiceCollection ModuleServices(string connection)
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
        services.AddScoped<IFeatureFlags>(sp => sp.GetRequiredService<PlatformQueries>());
        return services;
    }
}
