using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrightTime.Infrastructure.Persistence;

/// <summary>Registers a module context with the tenancy interceptors every module context needs.</summary>
public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Adds <typeparamref name="TContext"/>, scoped, with the tenant session interceptor and the
    /// transaction guard. The history table is per module, so modules migrate independently.
    /// </summary>
    public static IServiceCollection AddBrightTimeDbContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        string schema,
        string migrationsHistoryTable)
        where TContext : BrightTimeDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // One interceptor per request scope, shared by every module context in it.
        services.TryAddScoped<TenantSessionInterceptor>();

        services.AddDbContext<TContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .UseNetTopologySuite()
                .MigrationsHistoryTable(migrationsHistoryTable, schema))
            .AddInterceptors(
                sp.GetRequiredService<TenantSessionInterceptor>(),
                RequireTransactionInterceptor.Instance));

        return services;
    }
}
