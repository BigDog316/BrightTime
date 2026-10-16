using System.Text.Json;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Jobs;

/// <summary>A unit of background work to enqueue.</summary>
public sealed record JobRequest
{
    public required string JobType { get; init; }

    public TenantId? TenantId { get; init; }

    public string Queue { get; init; } = "default";

    /// <summary>Serialised to jsonb. Identifiers only, never personal data: the worker reads every tenant's jobs.</summary>
    public object? Payload { get; init; }

    public short Priority { get; init; } = 100;

    public short MaxAttempts { get; init; } = 10;

    /// <summary>
    /// Debounces: a second request with the same key collapses into one that has not started.
    /// One that is already running does not absorb it, so an edit made mid-run is not lost.
    /// </summary>
    public string? UniqueKey { get; init; }
}

/// <summary>Executes one job type. Registered with <c>AddJobHandler</c> and resolved per job, in a fresh scope.</summary>
public interface IJobHandler
{
    static abstract string JobType { get; }

    Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>What a handler is given: the job, its tenant (already set in the scope), and which attempt this is.</summary>
public sealed record JobExecutionContext(JobRecord Job, TenantId? TenantId, int Attempt)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public T ReadPayload<T>() =>
        JsonSerializer.Deserialize<T>(Job.Payload, PayloadOptions)
        ?? throw new InvalidOperationException($"Job {Job.Id} ({Job.JobType}) has an empty payload.");

    internal static string WritePayload(object? payload) =>
        payload is null ? "{}" : JsonSerializer.Serialize(payload, PayloadOptions);
}
