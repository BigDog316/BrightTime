namespace BrightTime.Infrastructure.Outbox;

/// <summary>
/// A row of <c>platform.outbox_message</c>: a module's domain event, written
/// in the same transaction as the state change that produced it.
/// </summary>
/// <remarks>
/// This is how modules talk without referencing each other.
/// <c>Opportunities</c> publishes <c>ListingPublished</c>; the search
/// projector and <c>Notifications</c> handle it. Neither handler can be
/// skipped by a crash between the commit and the dispatch, because the publish
/// <em>is</em> part of the commit.
/// </remarks>
public sealed class OutboxMessage
{
    public long Id { get; init; }

    public Guid MessageId { get; init; }

    public Guid TenantId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public required string SourceModule { get; init; }

    /// <summary>The contract type name, resolved to a handler by the dispatcher.</summary>
    public required string MessageType { get; init; }

    public required string Payload { get; init; }

    /// <summary>W3C traceparent, so a notification can be traced back to the request that caused it.</summary>
    public string? TraceContext { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public short Attempts { get; set; }

    public string? LastError { get; set; }
}
