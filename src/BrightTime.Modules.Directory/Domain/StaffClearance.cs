namespace BrightTime.Modules.Directory.Domain;

/// <summary>
/// A clearance's status and expiry.
/// </summary>
/// <remarks>
/// <para>
/// There is no property here that could hold a background-check report, a
/// disposition, a case number or a narrative, and no media reference. That is
/// not an omission to be fixed later: the platform deliberately does not hold
/// the underlying record, so a breach of this database cannot disclose one.
/// <see cref="VendorRefHash"/> is a keyed hash, enough to reconcile with the
/// vendor and useless on its own.
/// </para>
/// <para>
/// If a future requirement asks for the report, the answer is a link out to
/// the vendor's own system under the organisation's own credentials.
/// </para>
/// </remarks>
public sealed class StaffClearance
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Null for a tenant-wide clearance that travels with the person.</summary>
    public Guid? OrgId { get; init; }

    public required string ClearanceKind { get; init; }

    public required string Status { get; set; }

    public string? Vendor { get; set; }

    public byte[]? VendorRefHash { get; set; }

    public DateOnly? CompletedOn { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public Guid? RecordedBy { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public bool IsCurrentOn(DateOnly asOf) =>
        Status is "cleared" or "conditional" && (ExpiresOn is null || ExpiresOn >= asOf);
}
