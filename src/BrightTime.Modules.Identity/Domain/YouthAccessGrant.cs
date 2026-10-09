namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// Time-boxed, reason-bearing permission for one organisation to see one young
/// person's record.
/// </summary>
/// <remarks>
/// This is the table that stops a provider browsing youth it never served.
/// Role grants the <em>ability</em> to read youth records; a grant decides
/// <em>which</em>. When the grant ends — enrolment end plus the tenant's
/// retention grace, 180 days by default — the provider sees suppressed
/// aggregates only.
/// </remarks>
public sealed class YouthAccessGrant
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid YouthUserId { get; init; }

    public Guid OrgId { get; init; }

    public required string Basis { get; init; }

    public Guid? BasisRefId { get; init; }

    /// <summary>
    /// The fields this grant covers. Empty means the default youth-record set;
    /// support needs and contact details require explicit listing plus a
    /// family-profile share.
    /// </summary>
    public string[] Fields { get; set; } = [];

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public bool IsLiveAt(DateTimeOffset instant) =>
        StartsAt <= instant && (EndsAt is null || EndsAt > instant);
}
