using BrightTime.Modules.Identity.Authorization;

namespace BrightTime.Seed.Kettle;

internal sealed record SeedRole(Guid Id, string Name, IReadOnlyList<string> Permissions);

internal sealed record SeedMembership(Guid Id, Guid OrgId, string OrgRole);

internal sealed record SeedUser(Guid Id, string Email, IReadOnlyList<string> Roles, IReadOnlyList<SeedMembership> Memberships);

/// <summary>
/// Roles with their default permissions, and the accounts that sign in to the consoles:
/// an owner for every organisation, a staff member for the larger ones, and two network
/// organisers.
/// </summary>
/// <remarks>
/// No account has a password. Until Identity sign-in exists they are reached through the
/// development sign-in (decision D14), and afterwards through a password reset.
/// </remarks>
internal static class KettleAccounts
{
    public const string OrganiserEmail = "organiser@kettle.example.org";

    public static Guid OrganiserId { get; } = SeedIds.For("user/" + OrganiserEmail);

    public static Guid RoleId(string name) => SeedIds.For("role/" + name);

    public static IReadOnlyList<SeedRole> Roles() =>
    [
        .. BrightTimeRoles.DefaultPermissions
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new SeedRole(RoleId(r.Key), r.Key, r.Value)),
    ];

    public static IReadOnlyList<SeedUser> Users(IReadOnlyList<SeedOrganisation> organisations, IReadOnlyList<SeedSite> sites)
    {
        List<SeedUser> users =
        [
            Organiser(OrganiserEmail),
            Organiser("organiser2@kettle.example.org"),
        ];

        foreach (SeedOrganisation org in organisations)
        {
            string domain = $"{org.Slug}.example.org";
            users.Add(Member($"admin@{domain}", BrightTimeRoles.ProviderAdmin, org.Id, OrganisationRoles.Owner));

            if (sites.Count(s => s.OrgId == org.Id) > 1)
            {
                users.Add(Member($"staff@{domain}", BrightTimeRoles.ProgramStaff, org.Id, OrganisationRoles.Staff));
            }
        }

        return users;
    }

    private static SeedUser Organiser(string email) =>
        new(SeedIds.For("user/" + email), email, [BrightTimeRoles.NetworkOrganiser], []);

    private static SeedUser Member(string email, string role, Guid org, string orgRole) =>
        new(SeedIds.For("user/" + email), email, [role],
            [new SeedMembership(SeedIds.For($"membership/{email}/{org}"), org, orgRole)]);
}
