namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// A blocked relationship: custody orders, safety plans, moderation outcomes
/// and user blocks.
/// </summary>
/// <remarks>
/// Honoured in <em>both</em> directions, and by visibility as well as by
/// messaging. A restriction that only stopped outbound messages would still
/// let the restricted adult see which program a child attends, which is the
/// information that matters in a custody dispute.
/// </remarks>
public sealed class RestrictedContact
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ProtectedUserId { get; init; }

    public Guid? RestrictedUserId { get; init; }

    public Guid? RestrictedOrgId { get; init; }

    public required string ReasonKind { get; init; }

    public string? Note { get; set; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsLiveAt(DateTimeOffset instant) => ExpiresAt is null || ExpiresAt > instant;
}

public static class RestrictionReason
{
    public const string CustodyOrder = "custody_order";
    public const string SafetyPlan = "safety_plan";
    public const string UserBlock = "user_block";
    public const string ModerationAction = "moderation_action";
    public const string GuardianRequest = "guardian_request";
}
