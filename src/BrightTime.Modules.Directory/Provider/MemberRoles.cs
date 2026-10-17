namespace BrightTime.Modules.Directory.Provider;

/// <summary>
/// Roles within an organisation, as <c>ck_org_membership_role</c> allows them, highest first.
/// Mirrors Identity's <c>OrganisationRoles</c>, which this module may not reference.
/// </summary>
public static class MemberRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Staff = "staff";
    public const string Mentor = "mentor";
    public const string Viewer = "viewer";

    /// <summary>The roles a provider may give from the console; mentors arrive with the mentor programme.</summary>
    public static readonly string[] Assignable = [Owner, Admin, Staff, Viewer];

    public static int Rank(string role) => role switch
    {
        Owner => 50,
        Admin => 40,
        Staff => 30,
        Mentor => 20,
        Viewer => 10,
        _ => 0,
    };
}
