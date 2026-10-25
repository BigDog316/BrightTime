namespace BrightTime.Modules.Enrollment.Contracts;

/// <summary>Who a family member is to the family (<c>ck_family_member_role</c>).</summary>
public static class FamilyMemberRoles
{
    public const string Youth = "youth";
    public const string Child = "child";
    public const string Guardian = "guardian";
    public const string EmergencyContact = "emergency_contact";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>([Youth, Child, Guardian, EmergencyContact], StringComparer.Ordinal);

    /// <summary>A young person, whose record needs a date of birth (<c>ck_family_member_youth_needs_dob</c>).</summary>
    public static bool IsYoungPerson(string role) => role is Youth or Child;
}

/// <summary>The kinds of support a family may tell a program about (<c>ck_support_need_kind</c>).</summary>
public static class SupportNeedKinds
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        ["allergy", "medication", "mobility", "sensory", "communication", "dietary", "medical_other", "iep_504", "none_disclosed"],
        StringComparer.Ordinal);
}

/// <summary>What a profile share can cover (<c>ck_profile_share_known_fields</c>, D44).</summary>
public static class ProfileShareFields
{
    /// <summary>Household size, primary language and whether an interpreter is needed.</summary>
    public const string Household = "household";

    /// <summary>Each shared member's names and role.</summary>
    public const string MemberNames = "member_names";

    public const string MemberBirthDates = "member_birth_dates";

    public const string MemberGrade = "member_grade";

    public const string MemberPronouns = "member_pronouns";

    /// <summary>The household's contacts: emergency and pick-up.</summary>
    public const string Contacts = "contacts";

    /// <summary>
    /// The family's consent to show support needs marked for providers. Recorded now; read by an
    /// organisation only once it has an active enrolment (the table's own rule), which comes later.
    /// </summary>
    public const string SupportNeeds = "support_needs";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Household, MemberNames, MemberBirthDates, MemberGrade, MemberPronouns, Contacts, SupportNeeds],
        StringComparer.Ordinal);
}

/// <summary>An application's states (<c>ck_application_status</c>).</summary>
public static class ApplicationStatuses
{
    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string UnderReview = "under_review";
    public const string Waitlisted = "waitlisted";
    public const string Offered = "offered";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Withdrawn = "withdrawn";
    public const string Expired = "expired";
    public const string Rejected = "rejected";

    /// <summary>States a family may still withdraw from, giving back any seat or place held.</summary>
    public static IReadOnlySet<string> Withdrawable { get; } = new HashSet<string>([Submitted, UnderReview, Waitlisted, Offered, Accepted], StringComparer.Ordinal);
}

/// <summary>
/// The eligibility rule kinds this build evaluates (D46). The table allows more
/// (<c>ck_eligibility_rule_kind</c>); each arrives with the data it needs.
/// </summary>
public static class EligibilityRuleKinds
{
    public const string AgeAtStart = "age_at_start";
    public const string GradeRange = "grade_range";
    public const string NeighbourhoodIn = "neighbourhood_in";

    /// <summary>Holds an unrevoked badge of a class (D67).</summary>
    public const string BadgeRequired = "badge_required";

    /// <summary>Has completed a playlist (D67).</summary>
    public const string PlaylistCompleted = "playlist_completed";

    public static IReadOnlySet<string> Supported { get; } =
        new HashSet<string>([AgeAtStart, GradeRange, NeighbourhoodIn, BadgeRequired, PlaylistCompleted], StringComparer.Ordinal);
}

/// <summary>An enrolment row's kind (<c>ck_enrollment_seat_kind</c>).</summary>
public static class EnrollmentKinds
{
    public const string Enrolled = "enrolled";
    public const string Waitlist = "waitlist";
}

/// <summary>An enrolment row's states (<c>ck_enrollment_status</c>).</summary>
public static class EnrollmentStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Withdrawn = "withdrawn";
    public const string NoShow = "no_show";
    public const string Removed = "removed";
    public const string Expired = "expired";
}
