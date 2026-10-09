namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// Roles held within an organisation, matching
/// <c>ck_org_membership_role</c>, with a rank so that "at least admin" is a
/// comparison rather than a list of string equalities scattered around.
/// </summary>
public static class OrganisationRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Staff = "staff";
    public const string Mentor = "mentor";
    public const string Viewer = "viewer";

    private static readonly Dictionary<string, int> Ranks = new(StringComparer.Ordinal)
    {
        [Owner] = 50,
        [Admin] = 40,
        [Staff] = 30,
        [Mentor] = 20,
        [Viewer] = 10,
    };

    public static int RankOf(string? role) =>
        role is not null && Ranks.TryGetValue(role, out int rank) ? rank : 0;

    public static bool IsAtLeast(string? heldRole, string requiredRole) =>
        RankOf(heldRole) >= RankOf(requiredRole) && RankOf(heldRole) > 0;

    public static IReadOnlyList<string> All { get; } = [Owner, Admin, Staff, Mentor, Viewer];
}
