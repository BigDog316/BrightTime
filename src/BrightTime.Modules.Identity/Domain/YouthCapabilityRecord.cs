namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// The persisted capability row for a youth or child account
/// (<c>identity.youth_capability</c>).
/// </summary>
/// <remarks>
/// Stored rather than computed per request so that twelve modules do not each
/// re-derive consent logic — and so that
/// <c>ck_youth_capability_under13</c> can refuse the write if the under-13
/// floor is ever violated. A bug in the calculator therefore surfaces as a
/// constraint violation, not as a child posting in public.
/// </remarks>
public sealed class YouthCapabilityRecord
{
    public Guid UserId { get; init; }

    public Guid TenantId { get; init; }

    public bool IsUnder13 { get; set; }

    public bool HasAccountConsent { get; set; }

    public bool CanSelfEnroll { get; set; }

    public bool CanPostInCommunity { get; set; }

    public bool CanUploadMedia { get; set; }

    public bool CanPublishShowcase { get; set; }

    public bool CanReceiveSms { get; set; }

    public bool CanBeMessagedByOrg { get; set; }

    public DateTimeOffset RecomputedAt { get; set; }
}
