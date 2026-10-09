namespace BrightTime.Modules.Directory.Contracts;

/// <summary>
/// The clearances an organisation records against a staff member or mentor.
/// </summary>
/// <remarks>
/// Status and expiry only. There is deliberately no type anywhere in this
/// module that can hold a background-check report, a disposition, a case
/// number or a narrative, and no media reference on the clearance record. Who
/// decides that a conditional result is acceptable is the organisation's
/// responsibility and liability, not this platform's.
/// </remarks>
public enum ClearanceKind
{
    BackgroundCheck,
    Fingerprint,
    ChildAbuseRegistry,
    SexOffenderRegistry,
    MandatedReporterTraining,
    SafeguardingTraining,
    CprFirstAid,
    DrivingRecord,
}

public enum ClearanceStatus
{
    NotStarted,
    Pending,
    Cleared,

    /// <summary>Cleared with a condition the organisation accepted. Treated as cleared for access, and flagged in reports.</summary>
    Conditional,

    NotCleared,
    Expired,
    Withdrawn,
}

public static class ClearanceExtensions
{
    /// <summary>Matches <c>ck_staff_clearance_kind</c>.</summary>
    public static string ToDbValue(this ClearanceKind kind) => kind switch
    {
        ClearanceKind.BackgroundCheck => "background_check",
        ClearanceKind.Fingerprint => "fingerprint",
        ClearanceKind.ChildAbuseRegistry => "child_abuse_registry",
        ClearanceKind.SexOffenderRegistry => "sex_offender_registry",
        ClearanceKind.MandatedReporterTraining => "mandated_reporter_training",
        ClearanceKind.SafeguardingTraining => "safeguarding_training",
        ClearanceKind.CprFirstAid => "cpr_first_aid",
        ClearanceKind.DrivingRecord => "driving_record",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped clearance kind."),
    };

    /// <summary>Matches <c>ck_staff_clearance_status</c>.</summary>
    public static string ToDbValue(this ClearanceStatus status) => status switch
    {
        ClearanceStatus.NotStarted => "not_started",
        ClearanceStatus.Pending => "pending",
        ClearanceStatus.Cleared => "cleared",
        ClearanceStatus.Conditional => "conditional",
        ClearanceStatus.NotCleared => "not_cleared",
        ClearanceStatus.Expired => "expired",
        ClearanceStatus.Withdrawn => "withdrawn",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped clearance status."),
    };

    public static bool TryStatusFromDbValue(string? value, out ClearanceStatus status)
    {
        foreach (ClearanceStatus candidate in Enum.GetValues<ClearanceStatus>())
        {
            if (string.Equals(candidate.ToDbValue(), value, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = ClearanceStatus.NotStarted;
        return false;
    }

    public static bool TryKindFromDbValue(string? value, out ClearanceKind kind)
    {
        foreach (ClearanceKind candidate in Enum.GetValues<ClearanceKind>())
        {
            if (string.Equals(candidate.ToDbValue(), value, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = ClearanceKind.BackgroundCheck;
        return false;
    }

    /// <summary>
    /// The clearances a tenant requires before anyone may work directly with
    /// young people. The default; a tenant can add to it but not remove.
    /// </summary>
    public static IReadOnlyList<ClearanceKind> YouthFacingMinimum { get; } =
        [ClearanceKind.BackgroundCheck, ClearanceKind.MandatedReporterTraining];
}
