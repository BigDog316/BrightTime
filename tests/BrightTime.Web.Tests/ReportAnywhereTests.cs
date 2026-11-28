using System.Net;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Groups;
using BrightTime.Modules.Community.Moderation;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Reporting from anywhere (D128): anyone, signed in or not, reports an item on the public showcase
/// or a shown portfolio from its page; an organisation's staff report a young person's challenge
/// work; each joins the county's queue, critical ones emailed; a moderator reads each one's story
/// and takes it down: off the showcase, the work removed, the portfolio made private by Learning
/// and its owner told.
/// </summary>
/// <remarks>The provider is one with staff besides its owner and a builtin listing that has no space yet; the young people are the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ReportAnywhereTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Moderator = SeedIds.For("user/" + KettleAccounts.ModeratorEmail);

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    /// <summary>An approved provider with staff besides its owner, and a published builtin listing without a space.</summary>
    private async Task<(SeedOrganisation Provider, SeedListing Listing)> FreeListingAsync()
    {
        foreach (SeedOrganisation o in db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)
                     .Where(o => o.Status == "approved" && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")))
        {
            foreach (SeedListing l in db.Data.Listings.Where(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            {
                if (await db.ScalarAsync<long>("select count(*) from community.space where listing_id = @l", ("l", l.Id)) == 0)
                {
                    return (o, l);
                }
            }
        }

        throw new InvalidOperationException("No listing left without a space.");
    }

    /// <summary>A shown showcase item, of an approved project (the source isn't what's tested here).</summary>
    private async Task<Guid> ShownItemAsync(SeedOrganisation provider, Guid author, Guid staff, string title)
    {
        Guid item = UuidV7.NewId();
        await db.ScalarAsync<int>(
            """
            with p as (
              insert into learning.project (id, tenant_id, user_id, title, status, approved_by, approved_at)
              values (gen_random_uuid(), @t, @u, @title, 'approved', @s, now()) returning id)
            insert into community.showcase_item (id, tenant_id, org_id, author_user_id, project_id, title, body, org_name, display_name, approved_by, published_at)
            select @i, @t, @o, @u, p.id, @title, 'A kite that flew.', 'The organisation', 'Kit', @s, now() from p
            returning 1
            """,
            ("t", db.Data.TenantId), ("u", author), ("title", title), ("s", staff), ("i", item), ("o", provider.Id));
        return item;
    }

    private Task<long> ReportsOfAsync(string kind, Guid target) =>
        db.ScalarAsync<long>("select count(*) from community.report where target_kind = @k and target_id = @t", ("k", kind), ("t", target));

    private Task<Guid> LatestReportAsync(string kind, Guid target) =>
        db.ScalarAsync<Guid>("select id from community.report where target_kind = @k and target_id = @t order by created_at desc limit 1", ("k", kind), ("t", target));

    [Fact]
    public async Task Anyone_reports_a_showcase_item_and_a_shown_portfolio_and_a_moderator_takes_them_down()
    {
        RequireDatabase();
        (SeedOrganisation provider, _) = await FreeListingAsync();
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid author = await YouthEnrolment.AccountAsync(db, "reported");
        await db.ScalarAsync<int>("update identity.youth_capability set can_publish_showcase = true where user_id = @u; select 1;", ("u", author));
        string suffix = YouthEnrolment.Letters();
        Guid item = await ShownItemAsync(provider, author, staff, $"A kite ({suffix})");
        string slug = "kit-" + suffix;
        await db.ScalarAsync<int>(
            "insert into learning.learner_profile (user_id, tenant_id, display_name, headline, visibility, public_slug) values (@u, @t, 'Kit', @h, 'public', @s); select 1;",
            ("u", author), ("t", db.Data.TenantId), ("h", $"I make kites ({suffix})"), ("s", slug));

        // Someone not signed in reports the item from the showcase.
        using Browser anyone = db.NewBrowser();
        (await anyone.GetAsync("/showcase")).StatusCode.ShouldBe(HttpStatusCode.OK);
        anyone.Html.ShouldNotContain("public.report.");
        Shown(anyone.Html).ShouldContain("Report this");
        (await anyone.PostAsync("/showcase", "report-" + item.ToString("N"), ("reason", "harassment"), ("details", $"It mocks someone ({suffix}).")))
            .Headers.Location!.PathAndQuery.ShouldBe("/showcase?reported=yes");
        (await db.ScalarAsync<string>("select reason_code || '|' || state || '|' || (reporter_user_id is null) || '|' || details from community.report where target_kind = 'showcase_item' and target_id = @i", ("i", item)))
            .ShouldBe($"harassment|open|true|It mocks someone ({suffix}).");

        // And the portfolio from its page, as critical: every moderator is emailed.
        string page = "/portfolios/" + slug;
        (await anyone.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anyone.PostAsync(page, "report", ("reason", "self_harm"), ("details", "")))
            .Headers.Location!.PathAndQuery.ShouldBe(page + "?reported=critical");
        Guid portfolioReport = await LatestReportAsync("portfolio", author);
        (await db.ScalarAsync<long>("select count(*) from platform.job where job_type = 'community.critical_public_report' and payload::text like @r", ("r", "%" + portfolioReport + "%")))
            .ShouldBeGreaterThan(0);

        // Signed in, a visitor reports once while it waits; the author can't report their own; what isn't shown can't be.
        Guid visitor = db.Data.Users.First(u => u.Email == $"staff@{provider.Slug}.example.org").Id;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, visitor))
        {
            Reports reports = scope.ServiceProvider.GetRequiredService<Reports>();
            ReportReceipt first = (await reports.ReportShowcaseItemAsync(new UserId(visitor), item, "spam", null, Ct)).Value;
            (await reports.ReportShowcaseItemAsync(new UserId(visitor), item, "spam", null, Ct)).Value.Id.ShouldBe(first.Id);
            (await reports.ReportShowcaseItemAsync(null, Guid.NewGuid(), "spam", null, Ct)).Error!.Code.ShouldBe("showcase.not_shown");
            (await reports.ReportPortfolioAsync(null, "no-such-page", "spam", null, Ct)).Error!.Code.ShouldBe("portfolio.not_shown");
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, author))
        {
            (await scope.ServiceProvider.GetRequiredService<Reports>().ReportShowcaseItemAsync(new UserId(author), item, "spam", null, Ct))
                .Error!.Code.ShouldBe("showcase.not_shown");
        }

        (await ReportsOfAsync("showcase_item", item)).ShouldBe(2);

        // The moderator reads the item's story, by role: someone not signed in, someone signed in.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        string history = $"/organiser/moderation/history?kind=showcase_item&id={item:D}";
        (await moderator.GetAsync(history)).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldNotContain("moderation.");
        string story = Shown(moderator.Html);
        story.ShouldContain($"A kite ({suffix})");
        story.ShouldContain("someone not signed in");
        story.ShouldContain("someone signed in");

        // Removing it takes it off the showcase and settles both reports.
        Guid itemReport = await db.ScalarAsync<Guid>("select id from community.report where target_kind = 'showcase_item' and target_id = @i and reporter_user_id is null", ("i", item));
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, Moderator))
        {
            Reports reports = scope.ServiceProvider.GetRequiredService<Reports>();
            (await reports.RemoveAsync(new UserId(Moderator), itemReport, "Mocking a classmate.", Ct)).IsSuccess.ShouldBeTrue();
            (await reports.EscalateAsync(new UserId(Moderator), portfolioReport, "Told the county's child protection line; the young person's school too.", removeIt: true, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        (await db.ScalarAsync<string>("select (unpublished_at is not null) || '|' || (unpublished_by = @m) from community.showcase_item where id = @i", ("i", item), ("m", Moderator)))
            .ShouldBe("true|true");
        (await db.ScalarAsync<string>("select string_agg(state, ',') from community.report where target_kind = 'showcase_item' and target_id = @i", ("i", item)))
            .ShouldBe("actioned,actioned");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'reports.showcase_item_removed' and @r = any(subject_ids)", ("r", itemReport))).ShouldBe(1);

        // The portfolio goes private by Learning, on the worker, and its owner is told.
        (await db.ScalarAsync<long>("select count(*) from platform.outbox_message where message_type = @t and payload::text like @u",
            ("t", PortfolioTakenDown.MessageType), ("u", "%" + author + "%"))).ShouldBe(1);
        await using (AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(db.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
            PortfolioTakenDownHandler handler = new(scope.ServiceProvider.GetRequiredService<LearningDbContext>(), scope.ServiceProvider.GetRequiredService<ITenantContext>(),
                scope.ServiceProvider.GetRequiredService<AuditTrail>(), scope.ServiceProvider.GetRequiredService<IClock>());
            await handler.HandleAsync(new PortfolioTakenDown(author, portfolioReport), Ct);
            await handler.HandleAsync(new PortfolioTakenDown(author, portfolioReport), Ct);
        }

        (await db.ScalarAsync<string>("select visibility || '|' || public_slug from learning.learner_profile where user_id = @u", ("u", author))).ShouldBe("private|" + slug);
        (await db.ScalarAsync<long>("select count(*) from platform.outbox_message where payload::text like '%notify.portfolio.taken_down%' and payload::text like @u", ("u", "%" + author + "%")))
            .ShouldBe(1, "told once, however often it's delivered");
        (await anyone.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await db.ScalarAsync<string>("select string_agg(action, ',' order by action) from community.moderation_action where report_id = @r", ("r", portfolioReport)))
            .ShouldBe("escalate_external,remove");

        // The portfolio's story, with no organisation.
        (await moderator.GetAsync($"/organiser/moderation/history?kind=portfolio&id={author:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain($"I make kites ({suffix})");
        Shown(moderator.Html).ShouldContain("None: it's a young person's own page");
    }

    [Fact]
    public async Task Staff_report_a_young_persons_work_and_a_moderator_removes_it()
    {
        RequireDatabase();
        (SeedOrganisation provider, SeedListing listing) = await FreeListingAsync();
        string adminEmail = $"admin@{provider.Slug}.example.org";
        Guid owner = SeedIds.For($"user/{adminEmail}");
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        OrganisationId org = new(provider.Id);
        string suffix = YouthEnrolment.Letters();
        Guid ama = await YouthEnrolment.AccountAsync(db, "worry");
        await db.ScalarAsync<int>("update identity.youth_capability set can_post_in_community = true where user_id = @u; select 1;", ("u", ama));
        await YouthEnrolment.EnrolAsync(db, ama, owner, provider, listing, "Ama", linked: true);

        Guid space;
        Guid challenge;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Spaces spaces = scope.ServiceProvider.GetRequiredService<Spaces>();
            space = (await spaces.OpenAsync(org, new UserId(owner), listing.Id, null, null, Ct)).Value;
            (await spaces.AddStaffAsync(org, new UserId(owner), space, new UserId(staff), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            challenge = (await scope.ServiceProvider.GetRequiredService<Challenges>().SetAsync(new UserId(staff), space, $"How I feel ({suffix})", "Write about your week.", null, null, Ct)).Value;
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().SaveAsync(new UserId(ama), space, challenge, $"Some weeks I don't want to be here ({suffix}).", send: true, Ct))
                .Value.State.ShouldBe("submitted");
        }

        Guid work = await db.ScalarAsync<Guid>("select id from community.submission where challenge_id = @c and author_user_id = @u", ("c", challenge), ("u", ama));

        // Staff report it from the challenge's page, as critical.
        string page = $"/provider/orgs/{provider.Id}/spaces/{space:D}/challenges/{challenge:D}";
        using Browser staffer = db.NewBrowser();
        await staffer.SignInAsync($"staff@{provider.Slug}.example.org");
        (await staffer.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(staffer.Html).ShouldContain("Report this work to the county's moderators");
        (await staffer.PostAsync(page, "report-" + work.ToString("N"), ("reason", "self_harm"), ("details", "She's said this before.")))
            .Headers.Location!.PathAndQuery.ShouldBe(page + "?done=reported_critical");
        Guid report = await LatestReportAsync("submission", work);

        // Another organisation's staff can't; nor can its author.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Reports>().ReportSubmissionAsync(new UserId(staff), new OrganisationId(Guid.NewGuid()), work, "spam", null, Ct))
                .Error!.Code.ShouldBe("reports.work_not_found");
        }

        // The moderator reads it with the brief it answered, and escalates it, removing the work.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        (await moderator.GetAsync($"/organiser/moderation/history?kind=submission&id={work:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldNotContain("moderation.");
        Shown(moderator.Html).ShouldContain($"Some weeks I don't want to be here ({suffix}).");
        Shown(moderator.Html).ShouldContain($"How I feel ({suffix})");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, Moderator))
        {
            (await scope.ServiceProvider.GetRequiredService<Reports>().EscalateAsync(new UserId(Moderator), report, "Called the county's crisis line with the organisation's safeguarding lead.", removeIt: true, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        (await db.ScalarAsync<string>("select state from community.submission where id = @s", ("s", work))).ShouldBe("removed");

        // Staff see it removed, and give no more feedback on it.
        await staffer.GetAsync(page);
        Shown(staffer.Html).ShouldContain("removed by a moderator");
        staffer.Html.ShouldNotContain("feedback-" + work.ToString("N"));
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().GiveFeedbackAsync(new UserId(staff), space, challenge, work, "Thinking of you.", null, null, Ct))
                .Error!.Code.ShouldBe("challenges.submission_not_found");
        }

        // It shows in the county's recent decisions.
        await moderator.GetAsync("/organiser/moderation/history");
        moderator.Html.ShouldContain($"kind=submission&amp;id={work:D}");
    }
}
