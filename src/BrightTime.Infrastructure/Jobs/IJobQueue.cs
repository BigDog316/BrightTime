using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Jobs;

public sealed record JobRequest
{
    public required string JobType { get; init; }

    public TenantId? TenantId { get; init; }

    public string Queue { get; init; } = "default";

    public object? Payload { get; init; }

    public short Priority { get; init; } = 100;

    public DateTimeOffset? RunAfter { get; init; }

    public short MaxAttempts { get; init; } = 10;

    /// <summary>Set to debounce: a second enqueue with the same key while one is pending is a no-op.</summary>
    public string? UniqueKey { get; init; }
}

/// <summary>
/// Postgres-backed job queue. Claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> against the partial index
/// <c>ix_job_claim</c>, woken by <c>LISTEN/NOTIFY</c> so an idle worker does
/// not poll.
/// </summary>
/// <remarks>
/// Hand-rolled rather than Hangfire: see docs/plan/08-decisions.md D2. The
/// short version is tenant-awareness, audit integration and one schema to
/// review instead of two.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a queue. The rule exists to stop 'Queue' being used for non-queues.")]
public interface IJobQueue
{
    /// <summary>
    /// Enqueues work in the caller's transaction, so a job is never visible for
    /// a state change that rolled back. Returns false when a
    /// <see cref="JobRequest.UniqueKey"/> collapsed it into a pending job.
    /// </summary>
    Task<bool> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Executes one job type. Implementations are registered by <see cref="JobType"/> and resolved per claim.</summary>
public interface IJobHandler
{
    static abstract string JobType { get; }

    Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

public sealed record JobExecutionContext(JobRecord Job, TenantId TenantId, int Attempt);
