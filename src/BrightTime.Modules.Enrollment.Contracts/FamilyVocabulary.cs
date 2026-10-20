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
