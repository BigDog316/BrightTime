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
