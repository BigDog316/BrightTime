using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BrightTime.Modules.Platform.Media;

/// <summary>
/// Makes the smaller copies of public images added before D106, a batch at a time, each in its own
/// transaction; while a batch comes back full, the next is booked straight after. An image added
/// since gets its copies as it's added, so once a tenant's are done this finds nothing.
/// </summary>
public sealed partial class MediaSizesHandler(MediaLibrary media, PlatformDbContext db, ITenantContext tenant, ILogger<MediaSizesHandler> logger) : IJobHandler
{
    public static string JobType => "platform.media_sizes";

    /// <summary>How many images a run takes on.</summary>
    public const int Batch = 20;

    /// <summary>One run per tenant waits at a time.</summary>
    public static string KeyFor(TenantId tenant) => $"{JobType}:{tenant.Value:N}";

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RunAsync(bookNext: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sizes one batch; returns how many got copies and how many didn't decode.</summary>
    public async Task<(int Made, int Undecodable)> RunAsync(bool bookNext, CancellationToken cancellationToken = default)
    {
        List<Guid> due = await media.UnsizedAsync(Batch, cancellationToken).ConfigureAwait(false);
        int made = 0;
        int undecodable = 0;
        foreach (Guid id in due)
        {
            switch (await media.MakeSizesAsync(id, cancellationToken).ConfigureAwait(false))
            {
                case SizingOutcome.Made:
                    made++;
                    break;
                case SizingOutcome.Undecodable:
                    undecodable++;
                    LogUndecodable(logger, id);
                    break;
                default:
                    break;
            }
        }

        if (bookNext && due.Count == Batch)
        {
            await db.InTransactionAsync(ct => db.EnqueueJobAsync(new JobRequest
            {
                JobType = JobType,
                TenantId = tenant.TenantId,
                UniqueKey = KeyFor(tenant.TenantId),
                Priority = 200,
            }, ct), cancellationToken).ConfigureAwait(false);
        }

        if (due.Count > 0)
        {
            LogSized(logger, tenant.TenantId.Value, made, undecodable);
        }

        return (made, undecodable);
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Public image {MediaId} doesn't decode, so it has no smaller copies; the original stands in.")]
    private static partial void LogUndecodable(ILogger logger, Guid mediaId);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information, Message = "Made smaller copies of {Made} public images for tenant {Tenant}; {Undecodable} didn't decode.")]
    private static partial void LogSized(ILogger logger, Guid tenant, int made, int undecodable);
}

/// <summary>
/// Books each tenant's run of <see cref="MediaSizesHandler"/> when the worker starts. One already
/// waiting absorbs the booking, and a run that finds nothing ends there.
/// </summary>
public sealed partial class MediaSizesScheduler(IServiceScopeFactory scopes, NpgsqlDataSource dataSource, ILogger<MediaSizesScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (Guid tenant in await TenantsAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await BookAsync(scope.ServiceProvider, new TenantId(tenant), stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One tenant's failure must not stop the others'; the next start tries again.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, tenant, ex.GetType().Name);
            }
        }
    }

    /// <summary>Books the tenant's run for now, unless one is already waiting.</summary>
    public static Task<bool> BookAsync(IServiceProvider services, TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<TenantContext>().SetTenant(tenant, TimeZoneInfo.Utc, Locale.Default);
        PlatformDbContext db = services.GetRequiredService<PlatformDbContext>();
        return db.InTransactionAsync(ct => db.EnqueueJobAsync(new JobRequest
        {
            JobType = MediaSizesHandler.JobType,
            TenantId = tenant,
            UniqueKey = MediaSizesHandler.KeyFor(tenant),
            Priority = 200,
        }, ct), cancellationToken);
    }

    private async Task<List<Guid>> TenantsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand command = new("select distinct tenant_id from platform.tenant_domain", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<Guid> tenants = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            tenants.Add(reader.GetGuid(0));
        }

        return tenants;
    }

    [LoggerMessage(EventId = 6103, Level = LogLevel.Error, Message = "Booking the smaller copies of public images failed for tenant {Tenant}: {Exception}.")]
    private static partial void LogFailed(ILogger logger, Guid tenant, string exception);
}

public static class MediaSizeJobs
{
    /// <summary>The store, the library and the backfill of public images' copies, for the jobs worker (D106).</summary>
    public static IServiceCollection AddMediaSizeJobs(this IServiceCollection services)
    {
        services.AddScoped<IObjectStore, DatabaseObjectStore>();
        services.AddScoped<MediaLibrary>();

        // Learning's badges (D112) take the library by its contract; the worker awards playlists' badges.
        services.AddScoped<IMediaLibrary>(sp => sp.GetRequiredService<MediaLibrary>());
        services.AddJobHandler<MediaSizesHandler>();
        services.AddHostedService<MediaSizesScheduler>();
        return services;
    }
}
