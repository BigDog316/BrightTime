namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// A verifiable parental consent, as COPPA 16 CFR 312.5 requires.
/// </summary>
/// <remarks>
/// <see cref="ConsentTextHash"/> is the load-bearing column. It pins the exact
/// disclosure the guardian was shown, so changing the wording later cannot
/// retro-justify collection that the old wording did not cover. Without it,
/// "they consented" is an assertion; with it, it is evidence.
/// </remarks>
public sealed class ParentalConsent
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ChildUserId { get; init; }

    public Guid GuardianUserId { get; init; }

    public Guid GuardianLinkId { get; init; }

    public required string Scope { get; init; }

    public required string Method { get; init; }

    public DateTimeOffset GrantedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public Guid? EvidenceMediaId { get; set; }

    public required byte[] ConsentTextHash { get; init; }

    public byte[]? IpHash { get; init; }

    public byte[]? UserAgentHash { get; init; }

    public bool IsLiveAt(DateTimeOffset instant) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > instant);
}
