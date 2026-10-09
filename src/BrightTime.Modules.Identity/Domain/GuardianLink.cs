namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// A guardian's claim over a child's account, and how it was verified.
/// </summary>
/// <remarks>
/// The platform cannot adjudicate custody. What it can do is make access
/// legible and revocable: every link records who verified it and how, a 13+
/// young person can see every link on their own account, and revocation is
/// immediate. Where a link must be prevented rather than recorded, that is
/// <see cref="RestrictedContact"/>.
/// </remarks>
public sealed class GuardianLink
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid GuardianUserId { get; init; }

    public Guid ChildUserId { get; init; }

    public required string Relationship { get; set; }

    public string Status { get; set; } = GuardianLinkStatus.Pending;

    public string? VerificationMethod { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public Guid? VerifiedBy { get; set; }

    public bool IsPrimary { get; set; }

    public bool CanManageProfile { get; set; } = true;

    public bool CanEnroll { get; set; } = true;

    public bool CanViewActivity { get; set; } = true;

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public bool IsVerified => Status == GuardianLinkStatus.Verified && RevokedAt is null;
}

public static class GuardianLinkStatus
{
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
}

public static class GuardianRelationshipKind
{
    public const string Parent = "parent";
    public const string LegalGuardian = "legal_guardian";
    public const string FosterParent = "foster_parent";
    public const string KinshipCaregiver = "kinship_caregiver";
    public const string CaseWorker = "case_worker";
    public const string Other = "other";

    public static readonly string[] All =
        [Parent, LegalGuardian, FosterParent, KinshipCaregiver, CaseWorker, Other];
}
