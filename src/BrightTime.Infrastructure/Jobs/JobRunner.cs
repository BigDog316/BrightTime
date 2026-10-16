using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrightTime.Infrastructure.Jobs;

/// <summary>How the worker polls and how long it holds a job.</summary>
public sealed class JobRunnerOptions
{
    public string Queue { get; set; } = "default";

    public int BatchSize { get; set; } = 10;

    /// <summary>How long to wait when the queue is empty. Work found means no wait at all.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a claimed job is held before the reaper presumes the worker dead.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(5);

    public string WorkerId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}";
}

/// <summary>Maps a job type name to the handler that runs it.</summary>
public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, Type> _handlers = new(StringComparer.Ordinal);

    public void Register(string jobType, Type handlerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        ArgumentNullException.ThrowIfNull(handlerType);

        if (_handlers.TryGetValue(jobType, out Type? existing) && existing != handlerType)
        {
            throw new InvalidOperationException(
                $"Job type '{jobType}' is already handled by {existing.Name}; {handlerType.Name} cannot also claim it.");
        }

        _handlers[jobType] = handlerType;
    }

    public Type? Resolve(string jobType) => _handlers.GetValueOrDefault(jobType);
}

public static class JobRegistration
{
    /// <summary>Registers a handler under its <see cref="IJobHandler.JobType"/>.</summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services)
        where THandler : class, IJobHandler
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<THandler>();
        Registry(services).Register(THandler.JobType, typeof(THandler));
        return services;
    }

    private static JobHandlerRegistry Registry(IServiceCollection services)
    {
        ServiceDescriptor? existing = services.FirstOrDefault(d => d.ServiceType == typeof(JobHandlerRegistry));
        if (existing?.ImplementationInstance is JobHandlerRegistry registry)
        {
            return registry;
        }

        registry = new JobHandlerRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}

/// <summary>
/// The background worker. Reaps dead workers' jobs, claims due ones, and runs each in its own
/// scope with the job's tenant set, so module contexts apply row-level security as they would
/// for a request.
/// </summary>
/// <remarks>
/// A handler failure is recorded on the job and never stops the worker; one bad job must not
/// halt every tenant's queue. Polling rather than LISTEN/NOTIFY for now: the design calls for
/// NOTIFY to cut latency, and that is a follow-up, not a correctness gap.
/// </remarks>
public sealed partial class JobRunner(
    JobStore store,
    JobHandlerRegistry registry,
    IServiceScopeFactory scopes,
    IOptions<JobRunnerOptions> options,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly JobRunnerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerStarted(logger, _options.WorkerId, _options.Queue);

        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A database outage must not end the worker; it retries on the next poll.
                LoopFailed(logger, ex);
                processed = 0;
            }

            if (processed == 0)
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reaps, then claims and runs one batch. Returns how many jobs ran.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        int reaped = await store.ReapAsync(cancellationToken).ConfigureAwait(false);
        if (reaped > 0)
        {
            JobsReaped(logger, reaped);
        }

        IReadOnlyList<JobRecord> jobs = await store
            .ClaimAsync(_options.Queue, _options.WorkerId, _options.BatchSize, _options.LockDuration, cancellationToken)
            .ConfigureAwait(false);

        foreach (JobRecord job in jobs)
        {
            await RunJobAsync(job, cancellationToken).ConfigureAwait(false);
        }

        return jobs.Count;
    }

    /// <summary>Runs until the queue has nothing due. For tests and for draining on demand.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        int total = 0;
        for (int round = 0; round < 100; round++)
        {
            int processed = await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            if (processed == 0)
            {
                break;
            }

            total += processed;
        }

        return total;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A handler's failure is recorded on the job and retried; it must never stop the worker.")]
    private async Task RunJobAsync(JobRecord job, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        TenantId? tenant = job.TenantId is Guid id ? new TenantId(id) : null;
        if (tenant is { } t)
        {
            TenantContext context = scope.ServiceProvider.GetRequiredService<TenantContext>();
            context.SetTenant(t, TimeZoneInfo.Utc, Locale.Default);
            context.SetActor(UserId.None, ActorKind.Job);
        }

        try
        {
            Type handlerType = registry.Resolve(job.JobType)
                ?? throw new InvalidOperationException($"No handler is registered for job type '{job.JobType}'.");

            var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            await handler.ExecuteAsync(new JobExecutionContext(job, tenant, job.Attempts), cancellationToken)
                .ConfigureAwait(false);

            await store.CompleteAsync(job.Id, _options.WorkerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            string? state = await store
                .FailAsync(job.Id, _options.WorkerId, $"{ex.GetType().Name}: {ex.Message}", Backoff(job.Attempts), CancellationToken.None)
                .ConfigureAwait(false);

            JobFailed(logger, ex, job.Id, job.JobType, job.Attempts, state ?? "(no longer held)");
        }
    }

    /// <summary>5 s, 10 s, 20 s, … capped at one hour.</summary>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, Math.Max(attempt - 1, 0)), 3600));

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Job worker {WorkerId} started on queue {Queue}.")]
    private static partial void WorkerStarted(ILogger logger, string workerId, string queue);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Reaped {Count} job(s) whose worker stopped responding.")]
    private static partial void JobsReaped(ILogger logger, int count);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "Job {JobId} ({JobType}) failed on attempt {Attempt}; now {State}.")]
    private static partial void JobFailed(ILogger logger, Exception exception, Guid jobId, string jobType, int attempt, string state);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "The job loop failed; retrying on the next poll.")]
    private static partial void LoopFailed(ILogger logger, Exception exception);
}
