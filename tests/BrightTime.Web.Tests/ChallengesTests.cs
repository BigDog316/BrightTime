using System.Net;
using BrightTime.Modules.Community.Groups;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Challenges (D122): a space's staff set one from the console; its young people save work, send
/// it, and are told what to take out when screening keeps it back; another young person never sees
/// it, and staff never see a draft; staff send it back with feedback, the author changes it and
/// sends it again, staff accept it; a closed challenge takes no more work. Staff's brief is
/// screened too, and a young person sets nothing.
/// </summary>
/// <remarks>The provider is the eighth-last approved one with staff besides its owner (spaces take the fifth, post reports the seventh); the young people are the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ChallengesTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task Young_people_send_work_for_a_challenge_and_staff_send_it_back_and_accept_it()
    {
        RequireDatabase();
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(7).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        string adminEmail = $"admin@{provider.Slug}.example.org";
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid owner = SeedIds.For($"user/{adminEmail}");
        Guid ama = await YouthAsync(provider, listing, owner, "Ama");
        Guid kojo = await YouthAsync(provider, listing, owner, "Kojo");

        OrganisationId org = new(provider.Id);
        Guid space;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Spaces spaces = scope.ServiceProvider.GetRequiredService<Spaces>();
            space = (await spaces.OpenAsync(org, new UserId(owner), listing.Id, null, null, Ct)).Value;
            (await spaces.AddStaffAsync(org, new UserId(owner), space, new UserId(staff), Ct)).IsSuccess.ShouldBeTrue();
        }

        // The owner sets one from the console, due tomorrow.
        string suffix = YouthEnrolment.Letters();
        string page = $"/provider/orgs/{provider.Id}/spaces/{space:D}";
        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(adminEmail);
        (await admin.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldNotContain("provider.challenges.");
        string tomorrow = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        HttpResponseMessage set = await admin.PostAsync(page, "challenge", ("title", $"Build a bridge ({suffix})"), ("brief", "Out of paper, as long as you can."), ("due", tomorrow));
        string location = set.Headers.Location!.PathAndQuery;
        location.ShouldStartWith(page + "/challenges/");
        location.ShouldEndWith("?done=set");
        Guid challenge = Guid.Parse(location[(page.Length + "/challenges/".Length)..location.IndexOf('?', StringComparison.Ordinal)]);

        // Staff's words where young people read are screened, and young people set nothing.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().SetAsync(new UserId(staff), space, "Ask me", "Text me on 914-555-0123 for help.", null, null, Ct))
                .Error!.Code.ShouldBe("challenges.screened");
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Challenges challenges = scope.ServiceProvider.GetRequiredService<Challenges>();
            (await challenges.SetAsync(new UserId(ama), space, "Mine", "My own.", null, null, Ct)).Error!.Code.ShouldBe("challenges.not_staff");

            // She sees it, saves a draft, then tries to send it with her number: it's kept back, with why.
            ChallengeSummary listed = (await challenges.ListAsync(new UserId(ama), space, Ct)).Value.Single();
            (listed.Id, listed.Status, listed.MyState).ShouldBe((challenge, "open", (string?)null));
            (await challenges.SaveAsync(new UserId(ama), space, challenge, "A first idea.", send: false, Ct)).Value.State.ShouldBe("draft");
            SubmissionView kept = (await challenges.SaveAsync(new UserId(ama), space, challenge, "Mine held 2 m. Call 914-555-0142 to see it.", send: true, Ct)).Value;
            kept.State.ShouldBe("draft");
            kept.RefusedFor.ShouldNotBeNull().ShouldNotBeEmpty();
            (await challenges.GetAsync(new UserId(ama), space, challenge, Ct)).Value.Mine!.RefusedFor.ShouldNotBeNull().ShouldNotBeEmpty();
        }

        (await db.ScalarAsync<string>("select string_agg(decision, ',') from community.screening_result where target_kind = 'submission' and target_id = (select id from community.submission where challenge_id = @c and author_user_id = @u)",
            ("c", challenge), ("u", ama))).ShouldBe("hold");

        // Kojo writes a draft; he never sees hers, and the console never shows his.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, kojo))
        {
            Challenges challenges = scope.ServiceProvider.GetRequiredService<Challenges>();
            (await challenges.SaveAsync(new UserId(kojo), space, challenge, $"Kojo's draft ({suffix}).", send: false, Ct)).IsSuccess.ShouldBeTrue();
            ChallengeView his = (await challenges.GetAsync(new UserId(kojo), space, challenge, Ct)).Value;
            his.Mine!.Body.ShouldBe($"Kojo's draft ({suffix}).");
            his.Submissions.ShouldBeEmpty();
        }

        // She takes the number out and sends it; once sent, it waits.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Challenges challenges = scope.ServiceProvider.GetRequiredService<Challenges>();
            SubmissionView sent = (await challenges.SaveAsync(new UserId(ama), space, challenge, $"Mine held 2 m ({suffix}).", send: true, Ct)).Value;
            (sent.State, sent.RefusedFor).ShouldBe(("submitted", (IReadOnlyList<string>?)null));
            (await challenges.SaveAsync(new UserId(ama), space, challenge, "Changed my mind.", send: false, Ct)).Error!.Code.ShouldBe("challenges.sent");
        }

        // The owner reads it by her roster name and sends it back; Kojo's draft isn't there.
        string challengePage = $"{page}/challenges/{challenge:D}";
        (await admin.GetAsync(challengePage)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string shown = Shown(admin.Html);
        shown.ShouldContain($"Build a bridge ({suffix})");
        shown.ShouldContain("Ama M.");
        shown.ShouldContain($"Mine held 2 m ({suffix}).");
        shown.ShouldNotContain($"Kojo's draft ({suffix}).");
        admin.Html.ShouldNotContain("provider.challenges.");
        Guid work = await db.ScalarAsync<Guid>("select id from community.submission where challenge_id = @c and author_user_id = @u", ("c", challenge), ("u", ama));
        (await admin.PostAsync(challengePage, "feedback-" + work.ToString("N"), ("comment", "How did you test it?"), ("outcome", "needs_rework")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{challengePage}?done=sent_back");

        // She sees the feedback from staff, never by name, changes it and sends it again; it's accepted.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Challenges challenges = scope.ServiceProvider.GetRequiredService<Challenges>();
            ChallengeView mine = (await challenges.GetAsync(new UserId(ama), space, challenge, Ct)).Value;
            mine.CanSubmit.ShouldBeTrue();
            mine.Mine!.State.ShouldBe("needs_rework");
            mine.Mine.Feedback.Single().ShouldSatisfyAllConditions(f => f.From.ShouldBeNull(), f => f.Outcome.ShouldBe("needs_rework"), f => f.Comment.ShouldBe("How did you test it?"));
            (await challenges.SaveAsync(new UserId(ama), space, challenge, $"Held 2 m with a shoe on it ({suffix}).", send: true, Ct)).Value.State.ShouldBe("submitted");
        }

        await admin.GetAsync(challengePage);
        (await admin.PostAsync(challengePage, "feedback-" + work.ToString("N"), ("comment", "Brilliant."), ("outcome", "accepted")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{challengePage}?done=accepted");
        (await db.ScalarAsync<string>("select state from community.submission where id = @s", ("s", work))).ShouldBe("accepted");
        (await db.ScalarAsync<string>("select string_agg(coalesce(outcome, '-'), ',' order by created_at) from community.feedback where submission_id = @s", ("s", work)))
            .ShouldBe("needs_rework,accepted");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'challenges.submitted' and @s = any(subject_ids)", ("s", work))).ShouldBe(2);

        // Closed, it takes no more work.
        (await admin.PostAsync(challengePage, "close")).Headers.Location!.PathAndQuery.ShouldBe($"{challengePage}?done=closed");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, kojo))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().SaveAsync(new UserId(kojo), space, challenge, "Late.", send: true, Ct))
                .Error!.Code.ShouldBe("challenges.closed");
        }

        await admin.GetAsync(page);
        Shown(admin.Html).ShouldContain($"Build a bridge ({suffix})");
    }

    /// <summary>A young person with their own account, whose consent allows posting, enrolled in the listing.</summary>
    private async Task<Guid> YouthAsync(SeedOrganisation provider, SeedListing listing, Guid owner, string first)
    {
        Guid youth = await YouthEnrolment.AccountAsync(db, "challenge");
        await db.ScalarAsync<int>("update identity.youth_capability set can_post_in_community = true where user_id = @u; select 1;", ("u", youth));
        await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, first, linked: true);
        return youth;
    }
}
