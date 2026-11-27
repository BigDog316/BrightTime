using System.Net;
using BrightTime.Modules.Community.Groups;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.Web.Tests;

/// <summary>
/// Spaces (D120): staff open one for an activity and choose its staff; the activity's young people
/// join once two staff are in it and their consent allows posting, and leave when their enrolment
/// ends; what a young person writes is screened, and a held post waits for the county's
/// moderators, who publish or remove it. A muted adult doesn't post where young people read; an
/// archived space takes no posts. The database refuses a young person in a space with one adult.
/// </summary>
/// <remarks>The provider is the fifth-last approved one with staff besides its owner; the young people are the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class SpacesTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private (SeedOrganisation Provider, SeedListing Listing) Provider()
    {
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(4).First();
        return (provider, db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin"));
    }

    /// <summary>A young person with their own account, whose consent allows posting or not, enrolled in the listing.</summary>
    private async Task<(Guid User, Guid Enrolment)> EnrolledAsync(SeedOrganisation provider, SeedListing listing, Guid owner, string first, bool canPost)
    {
        Guid youth = await YouthEnrolment.AccountAsync(db, "space");
        await db.ScalarAsync<int>("update identity.youth_capability set can_post_in_community = @p where user_id = @u; select 1;", ("u", youth), ("p", canPost));
        Guid enrolment = await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, first, linked: true);
        return (youth, enrolment);
    }

    [Fact]
    public async Task Staff_open_a_space_its_young_people_join_and_a_held_post_waits_for_a_moderator()
    {
        RequireDatabase();
        (SeedOrganisation provider, SeedListing listing) = Provider();
        string adminEmail = $"admin@{provider.Slug}.example.org";
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid owner = SeedIds.For($"user/{adminEmail}");
        (Guid ama, Guid amaEnrolment) = await EnrolledAsync(provider, listing, owner, "Ama", canPost: true);
        (Guid kojo, _) = await EnrolledAsync(provider, listing, owner, "Kojo", canPost: false);

        // The admin opens a space for the activity; with one member of staff, no young person is in it.
        string spaces = $"/provider/orgs/{provider.Id}/spaces";
        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(adminEmail);
        (await admin.GetAsync(spaces)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldNotContain("provider.spaces.");
        HttpResponseMessage opened = await admin.PostAsync(spaces, "open", ("listing", listing.Id.ToString()), ("name", ""), ("description", "Our cohort's own corner."));
        string location = opened.Headers.Location!.PathAndQuery;
        location.ShouldStartWith(spaces + "/");
        Guid space = Guid.Parse(location[(spaces.Length + 1)..location.IndexOf('?', StringComparison.Ordinal)]);
        (await db.ScalarAsync<long>("select count(*) from community.space_member where space_id = @s and left_at is null", ("s", space))).ShouldBe(1);
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            (await scope.ServiceProvider.GetRequiredService<Spaces>().MineAsync(new UserId(ama), Ct)).ShouldBeEmpty();
        }

        // A second member of staff: the young person whose consent allows posting joins; the other doesn't.
        string page = $"{spaces}/{space:D}";
        await admin.GetAsync(page);
        (await admin.PostAsync(page, "add", ("person", staff.ToString()))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=added");
        (await db.ScalarAsync<string>("select string_agg(member_role || '/' || is_minor, ',' order by member_role, is_minor) from community.space_member where space_id = @s and left_at is null", ("s", space)))
            .ShouldBe("staff/false,staff/false,youth/true");
        (await db.ScalarAsync<long>("select count(*) from community.space_member where space_id = @s and user_id = @u", ("s", space), ("u", kojo))).ShouldBe(0);
        await admin.GetAsync(page);
        Shown(admin.Html).ShouldContain("Ama M.");

        // What she writes is screened: a hello is posted; her number is held, and only she sees it.
        string suffix = YouthEnrolment.Letters();
        Guid hello, held;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Spaces service = scope.ServiceProvider.GetRequiredService<Spaces>();
            (await service.MineAsync(new UserId(ama), Ct)).Single().Id.ShouldBe(space);
            Result<PostView> first = await service.PostAsync(new UserId(ama), space, $"Hi everyone ({suffix}).", null, Ct);
            first.IsSuccess.ShouldBeTrue(first.Error?.Code);
            first.Value.State.ShouldBe("published");
            hello = first.Value.Id;
            Result<PostView> second = await service.PostAsync(new UserId(ama), space, "Call me on 914-555-0142 after.", null, Ct);
            second.Value.State.ShouldBe("held");
            second.Value.HeldFor.ShouldNotBeNull().ShouldNotBeEmpty();
            held = second.Value.Id;
        }

        await admin.GetAsync(page);
        Shown(admin.Html).ShouldContain($"Hi everyone ({suffix}).");
        admin.Html.ShouldNotContain("914-555-0142");

        // Staff reply from the console; she sees it under her post, from staff, never by name.
        (await admin.PostAsync(page, "reply-" + hello.ToString("N"), ("body", $"Welcome, Ama ({suffix}).")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=posted");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            SpaceView view = (await scope.ServiceProvider.GetRequiredService<Spaces>().GetAsync(new UserId(ama), space, Ct)).Value;
            PostView mine = view.Posts.Single(p => p.Id == hello);
            mine.Replies.Single().ShouldSatisfyAllConditions(r => r.AuthorRole.ShouldBe("staff"), r => r.AuthorName.ShouldBeNull(), r => r.Body.ShouldBe($"Welcome, Ama ({suffix})."));
            view.Posts.Single(p => p.Id == held).State.ShouldBe("held");
            view.Members.Where(m => m.Role == "staff").ShouldAllBe(m => m.Name == null);
        }

        // The moderator publishes the held post, with a reason.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        (await moderator.GetAsync("/organiser/moderation")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("Call me on 914-555-0142 after.");
        (await moderator.PostAsync("/organiser/moderation", "post-" + held.ToString("N"), ("decision", "release"), ("reason", "")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("Give a reason of up to 1,000 characters.");
        await moderator.GetAsync("/organiser/moderation");
        (await moderator.PostAsync("/organiser/moderation", "post-" + held.ToString("N"), ("decision", "release"), ("reason", "A club's own number, shared by the club.")))
            .Headers.Location!.PathAndQuery.ShouldBe("/organiser/moderation?done=post_released");
        (await db.ScalarAsync<string>("select state from community.post where id = @p", ("p", held))).ShouldBe("published");
        (await db.ScalarAsync<string>("select action || '/' || target_kind from community.moderation_action where target_id = @p", ("p", held))).ShouldBe("publish/post");
        (await db.ScalarAsync<string>("select decision || '/' || provider from community.screening_result where target_id = @p", ("p", held))).ShouldBe("hold/local_pii");

        // The owner can't take the second member of staff out while she's in it.
        await admin.GetAsync(page);
        (await admin.PostAsync(page, "remove-" + staff.ToString("N"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(admin.Html).ShouldContain("needs at least two members of staff");

        // A muted adult doesn't post where she reads.
        await db.ScalarAsync<int>(
            "insert into community.person_restriction (id, tenant_id, user_id, kind, reason, imposed_by, ends_at) values (@id, @t, @u, 'mute', 'Testing.', @m, now() + interval '1 day'); select 1;",
            ("id", UuidV7.NewId()), ("t", db.Data.TenantId), ("u", staff), ("m", SeedIds.For("user/" + KettleAccounts.ModeratorEmail)));
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Spaces>().PostAsync(new UserId(staff), space, "See you Tuesday.", null, Ct)).Error!.Code.ShouldBe("spaces.muted");
        }

        await db.ScalarAsync<int>("update community.person_restriction set lifted_at = now(), lifted_by = imposed_by, lift_reason = 'Done.' where user_id = @u and lifted_at is null; select 1;", ("u", staff));

        // Her enrolment ends, and she leaves the space when anyone next opens it.
        await db.ScalarAsync<int>("update enrollment.enrollment set status = 'withdrawn', ended_at = now() where id = @e; select 1;", ("e", amaEnrolment));
        await admin.GetAsync(page);
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Spaces service = scope.ServiceProvider.GetRequiredService<Spaces>();
            (await service.MineAsync(new UserId(ama), Ct)).ShouldBeEmpty();
            (await service.GetAsync(new UserId(ama), space, Ct)).Error!.Code.ShouldBe("spaces.not_found");
        }

        (await db.ScalarAsync<bool>("select left_at is not null from community.space_member where space_id = @s and user_id = @u", ("s", space), ("u", ama))).ShouldBeTrue();

        // Archived, it takes no posts, and the activity can have a new one.
        (await admin.PostAsync(page, "archive")).Headers.Location!.PathAndQuery.ShouldBe($"{spaces}?done=archived");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Spaces service = scope.ServiceProvider.GetRequiredService<Spaces>();
            (await service.PostAsync(new UserId(staff), space, "Anyone there?", null, Ct)).Error!.Code.ShouldBe("spaces.archived");
            (await service.OpenAsync(new OrganisationId(provider.Id), new UserId(staff), listing.Id, null, null, Ct)).IsSuccess.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task The_database_refuses_a_young_person_in_a_space_with_one_adult()
    {
        RequireDatabase();
        (SeedOrganisation provider, _) = Provider();
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid youth = await YouthEnrolment.AccountAsync(db, "alone");
        Guid space = UuidV7.NewId();
        await db.ScalarAsync<int>(
            "insert into community.space (id, tenant_id, org_id, space_kind, name, status) values (@s, @t, @o, 'cohort', 'Alone', 'archived'); select 1;",
            ("s", space), ("t", db.Data.TenantId), ("o", provider.Id));

        PostgresException refused = await Should.ThrowAsync<PostgresException>(() => db.ScalarAsync<int>(
            """
            insert into community.space_member (id, tenant_id, space_id, user_id, member_role, is_minor) values (@a, @t, @s, @staff, 'staff', false);
            insert into community.space_member (id, tenant_id, space_id, user_id, member_role, is_minor) values (@b, @t, @s, @youth, 'youth', false);
            select 1;
            """,
            ("a", UuidV7.NewId()), ("b", UuidV7.NewId()), ("t", db.Data.TenantId), ("s", space), ("staff", staff), ("youth", youth)));
        refused.SqlState.ShouldBe("23514");
        refused.MessageText.ShouldContain("at least two adult members");
        (await db.ScalarAsync<long>("select count(*) from community.space_member where space_id = @s", ("s", space))).ShouldBe(0);
    }
}
