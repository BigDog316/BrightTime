namespace BrightTime.Modules.Directory.Contracts;

/// <summary>
/// An email to an organisation's owner or admin that a staff member's clearance expires soon or
/// has lapsed (D98). Directory's nightly sweep books it as a job; the host that can word and send
/// mail runs it. Identifiers and codes only: the job table is read across tenants.
/// </summary>
/// <param name="RecipientUserId">The owner or admin told.</param>
/// <param name="OrgId">The organisation they're told as.</param>
/// <param name="MembershipId">The staff member's membership there, for the link to their clearances.</param>
/// <param name="StaffUserId">Whose clearance it is.</param>
/// <param name="Kind">The clearance kind, as <c>ck_staff_clearance_kind</c>.</param>
/// <param name="ExpiresOn">The expiry date, in the county's days.</param>
/// <param name="Notice"><see cref="Expiring"/> or <see cref="Lapsed"/>.</param>
public sealed record ClearanceNotice(Guid RecipientUserId, Guid OrgId, Guid MembershipId, Guid StaffUserId, string Kind, DateOnly ExpiresOn, string Notice)
{
    public const string JobType = "directory.clearance_email";

    public const string Expiring = "expiring";

    public const string Lapsed = "lapsed";

    /// <summary>One email per person told, clearance, expiry and notice, however often the sweep is retried.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UniqueKey => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{JobType}:{RecipientUserId:N}:{StaffUserId:N}:{OrgId:N}:{Kind}:{ExpiresOn:yyyyMMdd}:{Notice}");
}
