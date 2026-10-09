using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BrightTime.Jobs;

/// <summary>
/// Placeholder lifetime so the worker host is startable while the queue
/// runner is built out. Replaced by the claim loop in the next slice of M1.
/// </summary>
internal sealed partial class JobHostLifetime(ILogger<JobHostLifetime> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "BrightTime job worker started. No queues are registered yet.")]
    private static partial void WorkerStarted(ILogger logger);
}
