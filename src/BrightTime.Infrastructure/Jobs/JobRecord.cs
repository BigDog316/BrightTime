namespace BrightTime.Infrastructure.Jobs;

/// <summary>A row of <c>platform.job</c>.</summary>
public sealed class JobRecord
{
    public Guid Id { get; init; }

    /// <summary>Null for platform-wide work such as partition maintenance.</summary>
    public Guid? TenantId { get; init; }

    public string Queue { get; init; } = "default";

    public required string JobType { get; init; }

    public string Payload { get; init; } = "{}";

    public string State { get; set; } = JobState.Ready;

    public short Priority { get; init; } = 100;

    public DateTimeOffset RunAfter { get; set; }

    public short Attempts { get; set; }

    public short MaxAttempts { get; init; } = 10;

    public string? LockedBy { get; set; }

    public DateTimeOffset? LockedAt { get; set; }

    public DateTimeOffset? LockExpiresAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Collapses duplicate work. <c>ux_job_unique_key</c> is partial on
    /// ready-or-running, so forty edits to one listing in a minute leave one
    /// queued reprojection rather than forty.
    /// </summary>
    public string? UniqueKey { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public static class JobState
{
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>Exhausted its attempts. Left in place for inspection rather than deleted.</summary>
    public const string Dead = "dead";
}
