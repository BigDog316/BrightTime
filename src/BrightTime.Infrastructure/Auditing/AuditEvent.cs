using BrightTime.Infrastructure.Tenancy;

namespace BrightTime.Infrastructure.Auditing;

/// <summary>
/// One row of <c>platform.audit_event</c>: a single <em>operation</em>, not a
/// single record.
/// </summary>
/// <remarks>
/// A roster read that touches forty youth writes one row carrying forty
/// subject ids, not forty rows. "Who read child X, and when" is still
/// answerable through the GIN index on <c>subject_ids</c>, at a fortieth of
/// the write volume. See docs/plan/08-decisions.md D11.
/// </remarks>
public sealed class AuditEvent
{
    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public Guid TenantId { get; init; }

    public Guid? ActorId { get; init; }

    public required string ActorKind { get; init; }

    public Guid? ActorOrgId { get; init; }

    /// <summary>Dotted action name, e.g. <c>enrollment.roster.read</c>.</summary>
    public required string Action { get; init; }

    public required string Outcome { get; init; }

    public string? SubjectKind { get; init; }

    public Guid[]? SubjectIds { get; init; }

    public int SubjectCount { get; init; }

    public string? Resource { get; init; }

    /// <summary>Required for a deny and for any break-glass read; <c>ck_audit_reason_required</c> enforces it.</summary>
    public string? Reason { get; init; }

    public string? RequestId { get; init; }

    public byte[]? IpHash { get; init; }

    public byte[]? UserAgentHash { get; init; }

    public string Detail { get; init; } = "{}";
}

public static class AuditOutcome
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Error = "error";
}

public static class AuditActorKind
{
    public static string ToDbValue(ActorKind kind) => kind switch
    {
        ActorKind.User => "user",
        ActorKind.Service => "service",
        ActorKind.Job => "job",
        ActorKind.Integration => "integration",
        ActorKind.Anonymous => "anonymous",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped actor kind."),
    };
}
