using BrightTime.Modules.Identity.Authorization;

namespace BrightTime.Modules.Identity.Tests;

/// <summary>
/// Invariants over the role and permission matrix. These are the ones that
/// would be quiet and serious if they drifted.
/// </summary>
public sealed class RolePermissionTests
{
    [Fact]
    public void No_role_is_seeded_with_a_permission_the_code_does_not_define()
    {
        // A tenant organiser can edit role permission sets, but only from the
        // defined vocabulary. A typo would otherwise seed a claim that grants
        // nothing and fails silently.
        foreach ((string role, IReadOnlyList<string> permissions) in BrightTimeRoles.DefaultPermissions)
        {
            foreach (string permission in permissions)
            {
                Permissions.All.ShouldContain(
                    permission,
                    $"Role '{role}' is seeded with '{permission}', which is not in Permissions.All.");
            }
        }
    }

    [Fact]
    public void The_platform_admin_has_no_access_to_youth_records()
    {
        // Platform administration is tenancy, deployment and incident
        // response. It is not a licence over children's data. Break-glass is a
        // separate, dual-authorised, time-boxed grant that writes an audit
        // event with a mandatory reason.
        IReadOnlyList<string> platformAdmin = BrightTimeRoles.DefaultPermissions[BrightTimeRoles.PlatformAdmin];

        platformAdmin.ShouldNotContain(Permissions.Youth.ReadRecord);
        platformAdmin.ShouldNotContain(Permissions.Youth.ReadSupportNeeds);
    }

    [Fact]
    public void Support_needs_are_reachable_only_by_a_provider_admin()
    {
        // The highest-sensitivity field group in the system: health,
        // disability, IEP/504. Program staff do not get it by default, and it
        // additionally needs a youth-access grant and a family-profile share.
        string[] rolesWithSupportNeeds =
        [
            .. BrightTimeRoles.DefaultPermissions
                .Where(kv => kv.Value.Contains(Permissions.Youth.ReadSupportNeeds, StringComparer.Ordinal))
                .Select(kv => kv.Key),
        ];

        rolesWithSupportNeeds.ShouldBe([BrightTimeRoles.ProviderAdmin]);
    }

    [Fact]
    public void A_child_role_can_browse_and_nothing_else()
    {
        // Everything an under-13 account does beyond browsing is mediated by a
        // guardian, so the role itself carries no write permission.
        IReadOnlyList<string> child = BrightTimeRoles.DefaultPermissions[BrightTimeRoles.Child];

        child.ShouldBe(
            [Permissions.Listings.Search, Permissions.Listings.ViewPublished, Permissions.Organisations.ViewPublic],
            ignoreOrder: true);
    }

    [Fact]
    public void Only_a_network_organiser_can_publish_or_review_a_listing()
    {
        // Provider self-publication is the thing organiser review exists to
        // prevent; a provider submits, an organiser publishes.
        foreach (string permission in new[] { Permissions.Listings.Publish, Permissions.Listings.Review })
        {
            string[] holders =
            [
                .. BrightTimeRoles.DefaultPermissions
                    .Where(kv => kv.Value.Contains(permission, StringComparer.Ordinal))
                    .Select(kv => kv.Key),
            ];

            holders.ShouldBe([BrightTimeRoles.NetworkOrganiser], $"for '{permission}'");
        }
    }

    [Fact]
    public void A_civic_viewer_and_a_researcher_get_no_write_permission_at_all()
    {
        string[] readOnly =
        [
            Permissions.Listings.Search,
            Permissions.Listings.ViewPublished,
            Permissions.Organisations.ViewPublic,
        ];

        BrightTimeRoles.DefaultPermissions[BrightTimeRoles.CivicViewer].ShouldBe(readOnly, ignoreOrder: true);
        BrightTimeRoles.DefaultPermissions[BrightTimeRoles.Researcher].ShouldBe(readOnly, ignoreOrder: true);
    }

    [Fact]
    public void Every_role_in_the_matrix_has_a_seeded_permission_set()
    {
        foreach (string role in BrightTimeRoles.All)
        {
            BrightTimeRoles.DefaultPermissions.ShouldContainKey(role);
        }
    }

    [Fact]
    public void Every_role_can_at_least_search_the_finder()
    {
        // The finder is public. No signed-in role should see less than an
        // anonymous visitor.
        foreach ((string role, IReadOnlyList<string> permissions) in BrightTimeRoles.DefaultPermissions)
        {
            permissions.ShouldContain(Permissions.Listings.Search, $"role '{role}'");
        }
    }

    [Theory]
    [InlineData(OrganisationRoles.Owner, OrganisationRoles.Admin, true)]
    [InlineData(OrganisationRoles.Admin, OrganisationRoles.Admin, true)]
    [InlineData(OrganisationRoles.Staff, OrganisationRoles.Admin, false)]
    [InlineData(OrganisationRoles.Mentor, OrganisationRoles.Staff, false)]
    [InlineData(OrganisationRoles.Viewer, OrganisationRoles.Mentor, false)]
    [InlineData(OrganisationRoles.Admin, OrganisationRoles.Viewer, true)]
    public void Organisation_roles_are_ranked(string held, string required, bool expected) =>
        OrganisationRoles.IsAtLeast(held, required).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not_a_role")]
    public void An_unknown_organisation_role_satisfies_nothing(string? held) =>
        OrganisationRoles.IsAtLeast(held, OrganisationRoles.Viewer).ShouldBeFalse();

    [Fact]
    public void A_membership_claim_round_trips()
    {
        Guid org = Guid.CreateVersion7();
        string claim = BrightTimeClaims.FormatMembership(org, OrganisationRoles.Admin);

        BrightTimeClaims.TryParseMembership(claim, out Guid parsedOrg, out string parsedRole).ShouldBeTrue();
        parsedOrg.ShouldBe(org);
        parsedRole.ShouldBe(OrganisationRoles.Admin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("not-a-guid:admin")]
    [InlineData("0196c3e8-0000-7000-8000-00000000000a:")]
    public void A_malformed_membership_claim_is_rejected(string? claim) =>
        BrightTimeClaims.TryParseMembership(claim, out _, out _).ShouldBeFalse();
}
