using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>Who is told of an organiser's decision on a listing (D102).</summary>
public sealed class ListingDecisionsTests
{
    private static readonly OrganisationId Org = new(Guid.NewGuid());

    private static OrganisationMembership Member(Guid user, string role) => new(Org, new UserId(user), role, DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_author_and_every_owner_and_admin_are_told_once()
    {
        Guid owner = Guid.NewGuid(), admin = Guid.NewGuid(), author = Guid.NewGuid(), staff = Guid.NewGuid();
        OrganisationMembership[] members = [Member(owner, "owner"), Member(admin, "admin"), Member(author, "staff"), Member(staff, "staff")];

        ListingDecisions.Recipients(members, author).ShouldBe([owner, admin, author]);
        ListingDecisions.Recipients(members, admin).ShouldBe([owner, admin], "an admin who saved it is told once");
    }

    [Fact]
    public void An_author_who_has_left_or_is_unknown_is_not_told()
    {
        Guid owner = Guid.NewGuid();
        OrganisationMembership[] members = [Member(owner, "owner"), Member(Guid.NewGuid(), "staff")];

        ListingDecisions.Recipients(members, Guid.NewGuid()).ShouldBe([owner]);
        ListingDecisions.Recipients(members, null).ShouldBe([owner]);
    }
}
