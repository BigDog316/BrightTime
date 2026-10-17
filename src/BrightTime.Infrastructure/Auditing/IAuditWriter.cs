using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Auditing;

/// <summary>
/// Records an access to youth data.
/// </summary>
/// <remarks>
/// <para>
/// Writes participate in the caller's transaction, so an audited change and
/// its audit entry commit or roll back together: there is no window in which a
/// youth record changed without a trace.
/// </para>
/// <para>
/// Reads are buffered and flushed at the end of the unit of work — a read does
/// not need to block on an insert, but it does need to be durable before the
/// response leaves, which is why the flush is awaited rather than fired and
/// forgotten.
/// </para>
/// </remarks>
public interface IAuditWriter
{
    /// <summary>Records an operation. Call once per operation, with every subject it touched.</summary>
    void Record(AuditAccess access);

    /// <summary>
    /// Writes one row inside <paramref name="db"/>'s open transaction, so a change and its
    /// trace commit or roll back together.
    /// </summary>
    Task RecordInTransactionAsync(Persistence.BrightTimeDbContext db, AuditAccess access, CancellationToken cancellationToken = default);

    /// <summary>Persists everything buffered for this unit of work.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>True when at least one youth-data access has been recorded; used by the endpoint filter that asserts the attribute was present.</summary>
    bool HasRecorded { get; }
}

/// <summary>A described access to youth data.</summary>
public sealed record AuditAccess
{
    public required string Action { get; init; }

    public string Outcome { get; init; } = AuditOutcome.Allow;

    public string? SubjectKind { get; init; }

    public IReadOnlyCollection<Guid> SubjectIds { get; init; } = [];

    public OrganisationId? ActorOrgId { get; init; }

    /// <summary>
    /// The actor, when it is not the request's: a sign-in names the person signing in, who is
    /// not yet the request's actor. Null means "whoever the request's actor is".
    /// </summary>
    public UserId? ActorId { get; init; }

    public string? Resource { get; init; }

    /// <summary>Mandatory on a deny or a break-glass read.</summary>
    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, string>? Detail { get; init; }
}
