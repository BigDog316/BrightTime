using System.Net;
using BrightTime.Modules.Community.Groups;
using BrightTime.Modules.Community.Moderation;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Reporting a post (D121): a member of a space reports a post someone else wrote there, from the
/// app or the console; it joins the county's queue beside reported messages, critical first; a
/// moderator not of the organisation removes it with a note, and its replies stay under a line
/// saying a moderator removed it. The post's history rebuilds what screening, reporters and
/// moderators did, and the recent decisions link to it. Nobody reports their own post, or one in a
/// space they aren't in.
/// </summary>
/// <remarks>The provider is the seventh-last approved one with staff besides its owner (spaces take the fifth); the young person is the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class PostReportsTests(ConsoleFixture db)
{
    private const string Queue = "/organiser/moderation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task A_reported_post_is_removed_by_a_moderator_its_replies_stay_and_its_history_tells_it()
    {
        RequireDatabase();
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(6).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        string adminEmail = $"admin@{provider.Slug}.example.org";
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid owner = SeedIds.For($"user/{adminEmail}");
        Guid ama = await YouthEnrolment.AccountAsync(db, "postreport");
        await db.ScalarAsync<int>("update identity.youth_capability set can_post_in_community = true where user_id = @u; select 1;", ("u", ama));
        await YouthEnrolment.EnrolAsync(db, ama, owner, provider, listing, "Ama", linked: true);
        Guid stranger = await YouthEnrolment.AccountAsync(db, "outsider");

        // The activity's space, with two staff; she joins as it's opened.
        OrganisationId org = new(provider.Id);
        Guid space;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Spaces spaces = scope.ServiceProvider.GetRequiredService<Spaces>();
            Result<Guid> opened = await spaces.OpenAsync(org, new UserId(owner), listing.Id, null, null, Ct);
            opened.IsSuccess.ShouldBeTrue(opened.Error?.Code);
            space = opened.Value;
            (await spaces.AddStaffAsync(org, new UserId(owner), space, new UserId(staff), Ct)).IsSuccess.ShouldBeTrue();
        }

        // Staff post; she replies, and posts her own.
        string suffix = YouthEnrolment.Letters();
        Guid staffPost, reply, hers;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            staffPost = (await scope.ServiceProvider.GetRequiredService<Spaces>().PostAsync(new UserId(staff), space, $"Stay behind after the session, just the two of us ({suffix}).", null, Ct)).Value.Id;
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Spaces spaces = scope.ServiceProvider.GetRequiredService<Spaces>();
            (await spaces.GetAsync(new UserId(ama), space, Ct)).IsSuccess.ShouldBeTrue();
            reply = (await spaces.PostAsync(new UserId(ama), space, $"Is that allowed? ({suffix})", staffPost, Ct)).Value.Id;
            hers = (await spaces.PostAsync(new UserId(ama), space, $"Who's coming Tuesday? ({suffix})", null, Ct)).Value.Id;
        }

        // She reports the staff post, critically; reporting again finds the first; nobody reports their own,
        // or a post in a space they aren't in.
        Guid critical;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Reports reports = scope.ServiceProvider.GetRequiredService<Reports>();
            Result<ReportReceipt> made = await reports.ReportPostAsync(new UserId(ama), staffPost, "grooming_concern", "He wants me to text him.", Ct);
            made.IsSuccess.ShouldBeTrue(made.Error?.Code);
            made.Value.Severity.ShouldBe("critical");
            critical = made.Value.Id;
            (await reports.ReportPostAsync(new UserId(ama), staffPost, "spam", null, Ct)).Value.Id.ShouldBe(critical);
            (await reports.ReportPostAsync(new UserId(ama), hers, "spam", null, Ct)).Error!.Code.ShouldBe("reports.post_not_found");
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, stranger))
        {
            (await scope.ServiceProvider.GetRequiredService<Reports>().ReportPostAsync(new UserId(stranger), staffPost, "spam", null, Ct))
                .Error!.Code.ShouldBe("reports.post_not_found");
        }

        (await db.ScalarAsync<long>("select count(*) from platform.job where job_type = 'community.critical_report_email' and payload->>'reportId' = @r",
            ("r", critical.ToString()))).ShouldBeGreaterThan(0);

        // Staff in the space report her post from the console.
        string page = $"/provider/orgs/{provider.Id}/spaces/{space:D}";
        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(adminEmail);
        (await admin.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("report-" + hers.ToString("N"));
        (await admin.PostAsync(page, "report-" + hers.ToString("N"), ("reason", "spam"), ("details", "")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=reported");
        Guid spam = await db.ScalarAsync<Guid>("select id from community.report where target_kind = 'post' and target_id = @p", ("p", hers));

        // In the queue, critical first, with the space and who wrote it by role.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        (await moderator.GetAsync(Queue)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string queue = Shown(moderator.Html);
        queue.ShouldContain($"Stay behind after the session, just the two of us ({suffix}).");
        queue.ShouldContain("The reported post");
        queue.ShouldContain($"history?post={staffPost:D}");
        queue.IndexOf($"Stay behind after the session, just the two of us ({suffix}).", StringComparison.Ordinal)
            .ShouldBeLessThan(queue.IndexOf($"Who's coming Tuesday? ({suffix})", StringComparison.Ordinal));

        // The moderator removes the staff post; the young person's report of it is settled.
        (await moderator.PostAsync(Queue, "take-" + critical.ToString("N"))).Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=taken_on");
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, "report-" + critical.ToString("N"), ("decision", "remove"), ("note", "Asked a young person to move off the platform.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=report_removed");
        (await db.ScalarAsync<string>("select state from community.post where id = @p", ("p", staffPost))).ShouldBe("removed");
        (await db.ScalarAsync<string>("select state from community.report where id = @r", ("r", critical))).ShouldBe("actioned");
        (await db.ScalarAsync<string>("select string_agg(action || '/' || target_kind, ',') from community.moderation_action where report_id = @r", ("r", critical))).ShouldBe("remove/post");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'reports.post_removed' and @r = any(subject_ids)", ("r", critical))).ShouldBe(1);

        // Her reply stays, under a line saying a moderator removed the post; its words are gone.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            SpaceView view = (await scope.ServiceProvider.GetRequiredService<Spaces>().GetAsync(new UserId(ama), space, Ct)).Value;
            PostView removed = view.Posts.Single(p => p.Id == staffPost);
            (removed.State, removed.Body, removed.AuthorName).ShouldBe(("removed", "", (string?)null));
            removed.Replies.Single().Id.ShouldBe(reply);
        }

        await admin.GetAsync(page);
        string console = Shown(admin.Html);
        console.ShouldContain("A moderator removed this post.");
        console.ShouldContain($"Is that allowed? ({suffix})");
        console.ShouldNotContain($"Stay behind after the session, just the two of us ({suffix}).");

        // Its history, in order; and the recent decisions link to it.
        (await moderator.GetAsync($"{Queue}/history?post={staffPost:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        string history = Shown(moderator.Html);
        history.ShouldContain("The post");
        history.ShouldContain("Screened and let through");
        int reported = history.IndexOf("Reported by the young person: Grooming concern", StringComparison.Ordinal);
        reported.ShouldBeGreaterThan(0, history);
        history.IndexOf("Removed by " + KettleAccounts.ModeratorEmail, StringComparison.Ordinal).ShouldBeGreaterThan(reported);
        history.ShouldContain("Asked a young person to move off the platform.");
        history.ShouldNotContain("moderation.");
        (await moderator.GetAsync($"{Queue}/history")).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldContain($"history?post={staffPost:D}");

        // The other is dismissed, and her post stays.
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, "report-" + spam.ToString("N"), ("decision", "dismiss"), ("note", "A fair question.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=dismissed");
        (await db.ScalarAsync<string>("select state from community.post where id = @p", ("p", hers))).ShouldBe("published");

        // Staff answer her post and she takes it down: the answer stays, under a line saying so.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Spaces>().PostAsync(new UserId(staff), space, $"Most of us ({suffix}).", hers, Ct)).Value.State.ShouldBe("published");
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            (await scope.ServiceProvider.GetRequiredService<Spaces>().DeleteAsync(new UserId(ama), space, hers, Ct)).IsSuccess.ShouldBeTrue();
        }

        await admin.GetAsync(page);
        console = Shown(admin.Html);
        console.ShouldContain("Its author took this post down.");
        console.ShouldContain($"Most of us ({suffix}).");
        console.ShouldNotContain($"Who's coming Tuesday? ({suffix})");

        // Its own organisation's staff can't read its history.
        HttpResponseMessage refused = await admin.GetAsync($"{Queue}/history?post={staffPost:D}");
        (refused.StatusCode == HttpStatusCode.Forbidden
         || (refused.StatusCode == HttpStatusCode.Redirect && refused.Headers.Location!.ToString().Contains("/access-denied", StringComparison.Ordinal))).ShouldBeTrue();
    }
}
