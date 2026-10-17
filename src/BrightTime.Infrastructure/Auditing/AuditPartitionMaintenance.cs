using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BrightTime.Infrastructure.Auditing;

/// <summary>
/// Keeps <c>platform.audit_event</c> partitioned ahead of time: once at start, then daily.
/// </summary>
/// <remarks>
/// A row that arrives before its month's partition lands in the default partition, and that
/// month's partition can then no longer be created without a detach-move-reattach under lock.
/// Four months ahead on a daily run leaves months of slack; a non-empty default partition is
/// logged as critical so it is fixed while it is still a handful of rows.
/// </remarks>
public sealed partial class AuditPartitionMaintenance(NpgsqlDataSource dataSource, ILogger<AuditPartitionMaintenance> logger) : BackgroundService
{
    public const int MonthsAhead = 4;

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Creates any missing partitions and returns how many rows sit in the default partition.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand ensure = new("select platform.ensure_audit_partitions(@months)", connection))
        {
            ensure.Parameters.Add(new NpgsqlParameter("months", MonthsAhead));
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using NpgsqlCommand count = new("select platform.audit_default_partition_rows()", connection);
        long stray = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (stray > 0)
        {
            LogStrayRows(logger, stray);
        }

        return stray;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                // Retried tomorrow; four months of slack makes one missed day harmless, and
                // the error is logged so a run of them is noticed.
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Critical,
        Message = "{Count} audit row(s) are in the default partition: a month arrived before its partition. Move them before that month's partition is needed.")]
    private static partial void LogStrayRows(ILogger logger, long count);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Error, Message = "Audit partition upkeep failed; it runs again tomorrow.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
