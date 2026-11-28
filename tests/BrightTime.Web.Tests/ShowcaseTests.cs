using System.Net;
using BrightTime.Modules.Community.Groups;
using BrightTime.Modules.Community.Showcasing;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.Web.Tests;

/// <summary>
/// The showcase (D124): an organisation's staff propose accepted challenge work and an approved
/// project from the console, with a caption that's screened; nothing is public until its author
/// says yes, under their portfolio's name and never one with their family name, and only while they
/// may publish there; no is for good; the public page shows what's shown, and drops it when the
/// project goes back to a draft or the author can no longer publish; the county takes it down, and
/// its author is told only that it was.
/// </summary>
/// <remarks>The provider is the ninth-last approved one with staff besides its owner (challenges take the eighth); the young people are the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ShowcaseTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task Staff_propose_work_its_author_says_yes_and_it_is_shown_until_taken_down()
    {
        RequireDatabase();
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(8).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        string adminEmail = $"admin@{provider.Slug}.example.org";
        Guid owner = SeedIds.For($"user/{adminEmail}");
        Guid ama = await YouthAsync(provider, listing, owner, "Ama", canShow: true);
        Guid kofi = await YouthAsync(provider, listing, owner, "Kofi", canShow: false);
        OrganisationId org = new(provider.Id);
        string suffix = YouthEnrolment.Letters();

        // Their work for a challenge, accepted; and an approved project of Ama's about the activity.
        Guid amaWork = await AcceptedWorkAsync(org, owner, listing, ama, kofi, suffix);
        Guid kofiWork = await db.ScalarAsync<Guid>("select s.id from community.submission s join community.challenge c on c.id = s.challenge_id where s.author_user_id = @u and c.title like @t",
            ("u", kofi), ("t", $"%({suffix})"));
        Guid project = await ApprovedProjectAsync(provider, listing, ama, owner, $"A rain garden ({suffix})");

        // Ama's portfolio name has her family name: she'll be told to change it.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            (await scope.ServiceProvider.GetRequiredService<Portfolios>().SaveAsync(new UserId(ama), new PortfolioInput("Ama Mensah", null, null, "private", null), Ct))
                .IsSuccess.ShouldBeTrue();
        }

        // The owner sees both as work to propose, and proposes them.
        string page = $"/provider/orgs/{provider.Id}/showcase";
        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(adminEmail);
        (await admin.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldNotContain("provider.showcase.");
        Shown(admin.Html).ShouldContain($"Bridge ({suffix})");
        Shown(admin.Html).ShouldContain($"A rain garden ({suffix})");
        (await admin.PostAsync(page, "propose-" + amaWork.ToString("N"), ("caption", "Built in the spring session.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=proposed");
        await admin.GetAsync(page);
        (await admin.PostAsync(page, "propose-" + project.ToString("N"), ("caption", "")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=proposed");

        // A caption with personal information is refused; each piece is asked about once.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Showcase showcase = scope.ServiceProvider.GetRequiredService<Showcase>();
            (await showcase.ProposeAsync(new UserId(owner), org, ShowcaseKinds.Submission, kofiWork, "Call him on 914-555-0142.", Ct)).Error!.Code.ShouldBe("showcase.screened");
            (await showcase.ProposeAsync(new UserId(owner), org, ShowcaseKinds.Submission, amaWork, null, Ct)).Error!.Code.ShouldBe("showcase.already");
            (await showcase.ProposeAsync(new UserId(owner), org, ShowcaseKinds.Project, Guid.NewGuid(), null, Ct)).Error!.Code.ShouldBe("showcase.not_found");
            (await showcase.ProposeAsync(new UserId(owner), org, ShowcaseKinds.Submission, kofiWork, null, Ct)).IsSuccess.ShouldBeTrue();
        }

        // Nothing is public yet.
        using Browser anyone = db.NewBrowser();
        (await anyone.GetAsync("/showcase")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(anyone.Html).ShouldNotContain($"({suffix})");
        anyone.Html.ShouldNotContain("showcase.page.");

        Guid workItem = await ItemAsync("submission_id", amaWork);
        Guid projectItem = await ItemAsync("project_id", project);
        Guid kofiItem = await ItemAsync("submission_id", kofiWork);
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Showcase showcase = scope.ServiceProvider.GetRequiredService<Showcase>();
            IReadOnlyList<ShowcaseEntry> mine = await showcase.MineAsync(new UserId(ama), Ct);
            mine.Select(m => (m.Id, m.State)).ShouldBe([(projectItem, ShowcaseStates.Waiting), (workItem, ShowcaseStates.Waiting)], ignoreOrder: true);
            mine.Single(m => m.Id == workItem).Caption.ShouldBe("Built in the spring session.");

            // Not under her family name: she changes her portfolio's, and says yes to both.
            (await showcase.AnswerAsync(new UserId(ama), workItem, show: true, Ct)).Error!.Code.ShouldBe("showcase.display_name_legal");
            (await scope.ServiceProvider.GetRequiredService<Portfolios>().SaveAsync(new UserId(ama), new PortfolioInput("Ama M.", null, null, "private", null), Ct))
                .IsSuccess.ShouldBeTrue();
            ShowcaseEntry shown = (await showcase.AnswerAsync(new UserId(ama), workItem, show: true, Ct)).Value;
            (shown.State, shown.DisplayName).ShouldBe((ShowcaseStates.Shown, "Ama M."));
            (await showcase.AnswerAsync(new UserId(ama), projectItem, show: true, Ct)).Value.State.ShouldBe(ShowcaseStates.Shown);
            (await showcase.AnswerAsync(new UserId(ama), workItem, show: false, Ct)).Error!.Code.ShouldBe("showcase.answered");
        }

        // Kofi may not publish to the showcase; he says no, for good.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, kofi))
        {
            Showcase showcase = scope.ServiceProvider.GetRequiredService<Showcase>();
            (await showcase.AnswerAsync(new UserId(kofi), kofiItem, show: true, Ct)).Error!.Code.ShouldBe("showcase.not_open");
            (await showcase.AnswerAsync(new UserId(kofi), kofiItem, show: false, Ct)).Value.State.ShouldBe(ShowcaseStates.Declined);
            (await showcase.AnswerAsync(new UserId(kofi), workItem, show: true, Ct)).Error!.Code.ShouldBe("showcase.item_not_found", "not his");
        }

        // The public page shows hers, under the name she chose; never Kofi's, never her family name.
        await anyone.GetAsync("/showcase");
        string publicPage = Shown(anyone.Html);
        publicPage.ShouldContain($"Bridge ({suffix})");
        publicPage.ShouldContain($"A rain garden ({suffix})");
        publicPage.ShouldContain("Ama M.");
        publicPage.ShouldContain("Built in the spring session.");
        publicPage.ShouldNotContain($"Kofi's bridge ({suffix})");
        publicPage.ShouldNotContain("Mensah");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'showcase.public_read' and @i = any(subject_ids)", ("i", workItem))).ShouldBeGreaterThan(0);

        // The project goes back to a draft (a change, D69): it leaves the showcase. So does everything
        // of hers while she can't publish there (a consent withdrawn, say).
        await db.ScalarAsync<int>("update learning.project set status = 'draft', approved_at = null, approved_by = null where id = @p; select 1;", ("p", project));
        await anyone.GetAsync("/showcase");
        Shown(anyone.Html).ShouldNotContain($"A rain garden ({suffix})");
        Shown(anyone.Html).ShouldContain($"Bridge ({suffix})");
        await db.ScalarAsync<int>("update identity.youth_capability set can_publish_showcase = false where user_id = @u; select 1;", ("u", ama));
        await anyone.GetAsync("/showcase");
        Shown(anyone.Html).ShouldNotContain($"Bridge ({suffix})");
        await db.ScalarAsync<int>("update identity.youth_capability set can_publish_showcase = true where user_id = @u; select 1;", ("u", ama));

        // The county's moderator takes it down, with a reason for staff.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        const string county = "/organiser/moderation/showcase";
        (await moderator.GetAsync(county)).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldNotContain("moderation.showcase.");
        Shown(moderator.Html).ShouldContain($"Bridge ({suffix})");
        (await moderator.PostAsync(county, "take-down-" + workItem.ToString("N"), ("reason", "A classmate's face is in the photo it describes.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{county}?done=taken_down");
        await anyone.GetAsync("/showcase");
        Shown(anyone.Html).ShouldNotContain($"Bridge ({suffix})");

        // She's told it was taken down, not why; staff see why.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Showcase showcase = scope.ServiceProvider.GetRequiredService<Showcase>();
            ShowcaseEntry taken = (await showcase.MineAsync(new UserId(ama), Ct)).Single(m => m.Id == workItem);
            (taken.State, taken.EndedByAuthor, taken.Reason).ShouldBe((ShowcaseStates.TakenDown, false, (string?)null));
            (await showcase.TakeDownMineAsync(new UserId(ama), workItem, Ct)).Error!.Code.ShouldBe("showcase.ended");
            (await showcase.TakeDownMineAsync(new UserId(ama), projectItem, Ct)).IsSuccess.ShouldBeTrue();
        }

        await admin.GetAsync(page);
        Shown(admin.Html).ShouldContain("A classmate's face is in the photo it describes.");
        (await db.ScalarAsync<string>(
            "select string_agg(action, ',' order by action) from platform.audit_event where action in ('showcase.proposed','showcase.published','showcase.declined','showcase.taken_down') and subject_ids && array[@a, @b, @c]",
            ("a", workItem), ("b", projectItem), ("c", kofiItem)))
            .ShouldBe("showcase.declined,showcase.proposed,showcase.proposed,showcase.proposed,showcase.published,showcase.published,showcase.taken_down,showcase.taken_down");
        (await db.ScalarAsync<string>("select detail ->> 'by' from platform.audit_event where action = 'showcase.taken_down' and @a = any(subject_ids)", ("a", workItem))).ShouldBe("county");
    }

    [Fact]
    public async Task A_guardian_managed_authors_work_is_never_published_without_a_recorded_consent()
    {
        RequireDatabase();

        // The schema's own check (ck_showcase_minor_consent), whatever the application does.
        // Checks come before keys, so the work needn't exist.
        SeedOrganisation provider = db.Data.Organisations.First(o => o.Status == "approved");
        Guid someone = db.Data.Users[0].Id;
        PostgresException refused = await Should.ThrowAsync<PostgresException>(() => db.ScalarAsync<int>(
            """
            insert into community.showcase_item (id, tenant_id, org_id, author_user_id, submission_id, title, body, org_name, display_name, approved_by, guardian_managed, published_at)
            values (gen_random_uuid(), @t, @o, @u, gen_random_uuid(), 'T', 'B', 'O', 'Name', @u, true, now())
            returning 1
            """, ("t", db.Data.TenantId), ("o", provider.Id), ("u", someone)));
        refused.ConstraintName.ShouldBe("ck_showcase_minor_consent");
    }

    private Task<Guid> ItemAsync(string column, Guid source) =>
        db.ScalarAsync<Guid>($"select id from community.showcase_item where {column} = @s", ("s", source));

    /// <summary>A space for the activity with a challenge; each young person's work sent, and both accepted.</summary>
    private async Task<Guid> AcceptedWorkAsync(OrganisationId org, Guid owner, SeedListing listing, Guid ama, Guid kofi, string suffix)
    {
        Guid space;
        Guid challenge;
        Guid staff = SeedIds.For($"user/staff@{db.Data.Organisations.Single(o => o.Id == org.Value).Slug}.example.org");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Spaces spaces = scope.ServiceProvider.GetRequiredService<Spaces>();
            space = (await spaces.OpenAsync(org, new UserId(owner), listing.Id, null, null, Ct)).Value;
            (await spaces.AddStaffAsync(org, new UserId(owner), space, new UserId(staff), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            challenge = (await scope.ServiceProvider.GetRequiredService<Challenges>().SetAsync(new UserId(staff), space, $"Bridge ({suffix})", "Out of paper.", null, null, Ct)).Value;
        }

        foreach ((Guid youth, string body) in new[] { (ama, $"Mine held 2 m ({suffix})."), (kofi, $"Kofi's bridge ({suffix}).") })
        {
            await using AsyncServiceScope scope = YouthEnrolment.As(db, youth);
            (await scope.ServiceProvider.GetRequiredService<Challenges>().SaveAsync(new UserId(youth), space, challenge, body, send: true, Ct)).Value.State.ShouldBe("submitted");
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Challenges challenges = scope.ServiceProvider.GetRequiredService<Challenges>();
            foreach (Guid youth in new[] { ama, kofi })
            {
                Guid work = await db.ScalarAsync<Guid>("select id from community.submission where challenge_id = @c and author_user_id = @u", ("c", challenge), ("u", youth));
                (await challenges.GiveFeedbackAsync(new UserId(staff), space, challenge, work, "Lovely.", "accepted", null, Ct)).IsSuccess.ShouldBeTrue();
            }
        }

        return await db.ScalarAsync<Guid>("select id from community.submission where challenge_id = @c and author_user_id = @u", ("c", challenge), ("u", ama));
    }

    /// <summary>A project the young person made about the activity (a participation record with the organisation), approved by its owner.</summary>
    private async Task<Guid> ApprovedProjectAsync(SeedOrganisation provider, SeedListing listing, Guid youth, Guid owner, string title)
    {
        Guid project = UuidV7.NewId();
        await db.ScalarAsync<int>(
            """
            insert into learning.participation_record (id, tenant_id, user_id, listing_id, org_id, title_snapshot, org_name_snapshot, started_on, source)
            values (@r, @t, @u, @l, @o, 'The activity', 'The organisation', current_date - 30, 'provider_entry');
            insert into learning.project (id, tenant_id, user_id, title, description, listing_id, status, approved_by, approved_at)
            values (@p, @t, @u, @title, 'Planted where the water runs off the car park.', @l, 'approved', @a, now());
            select 1;
            """,
            ("r", UuidV7.NewId()), ("t", db.Data.TenantId), ("u", youth), ("l", listing.Id), ("o", provider.Id), ("p", project), ("title", title), ("a", owner));
        return project;
    }

    /// <summary>A young person with their own account, enrolled in the listing; <paramref name="canShow"/> says whether they may publish to the showcase.</summary>
    private async Task<Guid> YouthAsync(SeedOrganisation provider, SeedListing listing, Guid owner, string first, bool canShow)
    {
        Guid youth = await YouthEnrolment.AccountAsync(db, "showcase");
        await db.ScalarAsync<int>("update identity.youth_capability set can_post_in_community = true, can_publish_showcase = @s where user_id = @u; select 1;", ("u", youth), ("s", canShow));
        await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, first, linked: true);
        return youth;
    }
}
