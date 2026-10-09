namespace BrightTime.Modules.Directory.Domain;

/// <summary>Someone's role at one organisation.</summary>
public sealed class OrgMembership
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public Guid UserId { get; init; }

    public required string OrgRole { get; set; }

    public string? Title { get; set; }

    public string Status { get; set; } = MembershipStatus.Invited;

    public Guid? InvitedBy { get; init; }

    public DateTimeOffset InvitedAt { get; init; }

    public DateTimeOffset? JoinedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? EndedReason { get; set; }

    public bool IsActive => Status == MembershipStatus.Active && EndedAt is null;
}

public static class MembershipStatus
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Ended = "ended";
    public const string Declined = "declined";
}
