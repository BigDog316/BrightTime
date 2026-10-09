namespace BrightTime.Modules.Identity.Contracts;

/// <summary>
/// Why an organisation may see a particular young person's record.
/// </summary>
/// <remarks>
/// Access is never a property of a role. A provider admin may read the youth
/// their organisation currently serves, which is what a grant records: a
/// reason, a start, and an end. After the end the provider sees suppressed
/// aggregates only.
/// </remarks>
public enum YouthAccessBasis
{
    Application,
    Enrollment,
    Waitlist,
    CohortMembership,
    GigAssignment,
    DistrictRoster,

    /// <summary>A guardian deliberately shared the record with this organisation.</summary>
    GuardianGrant,
}

public static class YouthAccessBasisExtensions
{
    /// <summary>Matches <c>ck_youth_access_basis</c>.</summary>
    public static string ToDbValue(this YouthAccessBasis basis) => basis switch
    {
        YouthAccessBasis.Application => "application",
        YouthAccessBasis.Enrollment => "enrollment",
        YouthAccessBasis.Waitlist => "waitlist",
        YouthAccessBasis.CohortMembership => "cohort_membership",
        YouthAccessBasis.GigAssignment => "gig_assignment",
        YouthAccessBasis.DistrictRoster => "district_roster",
        YouthAccessBasis.GuardianGrant => "guardian_grant",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Unmapped access basis."),
    };
}
