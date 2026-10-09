namespace BrightTime.Modules.Directory.Domain;

/// <summary>
/// One transition in an organisation's vetting history.
/// </summary>
/// <remarks>
/// Append-only in the schema, by revoked grants and by a statement trigger. A
/// provider asking "why were we rejected in March" and an organiser asking
/// "who approved this" need the same record, and neither is served by a status
/// column that only remembers the present.
/// </remarks>
public sealed class OrganisationVettingEvent
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public string? FromStatus { get; init; }

    public required string ToStatus { get; init; }

    public Guid? ActorId { get; init; }

    public string? Note { get; init; }

    /// <summary>The checklist as it stood at this transition, as jsonb.</summary>
    public string Checklist { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
