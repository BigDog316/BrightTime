using System.Net;
using BrightTime.Modules.Community.Groups;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Rubrics (D125): an organisation's admin writes one in the console, and its staff can't; what
/// young people read is screened; staff set a challenge with it from the space's page, and an
/// archived one isn't offered; feedback scores every criterion or none, within the scale; the young
/// person sees the criteria and their weighted total; once scored with, the rubric is fixed.
/// </summary>
/// <remarks>The provider is the tenth-last approved one with staff besides its owner (the showcase takes the ninth); the young person is the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class RubricsTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task An_admin_writes_a_rubric_staff_score_work_with_it_and_then_it_is_fixed()
    {
        RequireDatabase();
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(9).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        string adminEmail = $"admin@{provider.Slug}.example.org";
        string staffEmail = $"staff@{provider.Slug}.example.org";
        Guid owner = SeedIds.For($"user/{adminEmail}");
        Guid staff = SeedIds.For($"user/{staffEmail}");
        OrganisationId org = new(provider.Id);
        string suffix = YouthEnrolment.Letters();

        Guid ama = await YouthEnrolment.AccountAsync(db, "rubric");
        await db.ScalarAsync<int>("update identity.youth_capability set can_post_in_community = true where user_id = @u; select 1;", ("u", ama));
        await YouthEnrolment.EnrolAsync(db, ama, owner, provider, listing, "Ama", linked: true);

        // The owner writes one: strength counts twice.
        string rubrics = $"/provider/orgs/{provider.Id}/rubrics";
        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(adminEmail);
        (await admin.GetAsync(rubrics)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldNotContain("provider.rubrics.");
        (await admin.PostAsync(rubrics, "rubric", ("name", $"Bridges ({suffix})"), ("scale", "4"),
                ("label-0", "Strength"), ("descriptor-0", "Holds weight without bending."), ("weight-0", "2"),
                ("label-1", "Neatness"), ("descriptor-1", ""), ("weight-1", "")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{rubrics}?done=saved");
        Guid rubric = await db.ScalarAsync<Guid>("select id from community.rubric where org_id = @o and name = @n", ("o", provider.Id), ("n", $"Bridges ({suffix})"));
        (await db.ScalarAsync<string>("select string_agg(code || ':' || label || ':' || weight, ',' order by sort_order) from community.rubric_criterion where rubric_id = @r", ("r", rubric)))
            .ShouldBe("c1:Strength:2.00,c2:Neatness:1.00");

        // Programme staff score with rubrics, but don't write them.
        using (Browser staffer = db.NewBrowser())
        {
            await staffer.SignInAsync(staffEmail);
            (await staffer.GetAsync(rubrics)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        Guid spare;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Rubrics service = scope.ServiceProvider.GetRequiredService<Rubrics>();
            (await service.SaveAsync(new UserId(owner), org, null, "Ask", 4, [new("Text me on 914-555-0123", null, null)], Ct)).Error!.Code.ShouldBe("rubrics.screened");
            (await service.SaveAsync(new UserId(owner), org, null, "Too high", 11, [new("Strength", null, null)], Ct)).Error!.Code.ShouldBe("rubrics.scale");
            (await service.SaveAsync(new UserId(owner), org, null, "Empty", 4, [new("", "", null)], Ct)).Error!.Code.ShouldBe("rubrics.criteria");
            (await service.SaveAsync(new UserId(owner), org, null, "Heavy", 4, [new("Strength", null, 11m)], Ct)).Error!.Code.ShouldBe("rubrics.criterion");

            // A second, archived: it isn't offered.
            spare = (await service.SaveAsync(new UserId(owner), org, null, $"Old ({suffix})", 3, [new("Effort", null, null)], Ct)).Value;
            (await service.ArchiveAsync(org, spare, archived: true, Ct)).IsSuccess.ShouldBeTrue();
            (await service.ListAsync(org, includeArchived: false, Ct)).ShouldNotContain(r => r.Id == spare);
        }

        Guid space;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Spaces spaces = scope.ServiceProvider.GetRequiredService<Spaces>();
            space = (await spaces.OpenAsync(org, new UserId(owner), listing.Id, null, null, Ct)).Value;
            (await spaces.AddStaffAsync(org, new UserId(owner), space, new UserId(staff), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().SetAsync(new UserId(staff), space, "Old", "Effort.", null, spare, Ct))
                .Error!.Code.ShouldBe("challenges.rubric");
        }

        // Staff set a challenge with it from the space's page.
        using Browser staffBrowser = db.NewBrowser();
        await staffBrowser.SignInAsync(staffEmail);
        string spacePage = $"/provider/orgs/{provider.Id}/spaces/{space:D}";
        (await staffBrowser.GetAsync(spacePage)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(staffBrowser.Html).ShouldContain($"Bridges ({suffix})");
        Shown(staffBrowser.Html).ShouldNotContain($"Old ({suffix})");
        string location = (await staffBrowser.PostAsync(spacePage, "challenge", ("title", $"Bridge ({suffix})"), ("brief", "Out of paper."), ("due", ""), ("rubric", rubric.ToString())))
            .Headers.Location!.PathAndQuery;
        Guid challenge = Guid.Parse(location[(spacePage.Length + "/challenges/".Length)..location.IndexOf('?', StringComparison.Ordinal)]);
        (await db.ScalarAsync<Guid>("select rubric_id from community.challenge where id = @c", ("c", challenge))).ShouldBe(rubric);

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            Challenges challenges = scope.ServiceProvider.GetRequiredService<Challenges>();
            ChallengeView seen = (await challenges.GetAsync(new UserId(ama), space, challenge, Ct)).Value;
            seen.Rubric!.Criteria.Select(c => (c.Label, c.Weight)).ShouldBe([("Strength", 2m), ("Neatness", 1m)]);
            (await challenges.SaveAsync(new UserId(ama), space, challenge, $"Mine held 2 m ({suffix}).", send: true, Ct)).Value.State.ShouldBe("submitted");
        }

        Guid work = await db.ScalarAsync<Guid>("select id from community.submission where challenge_id = @c and author_user_id = @u", ("c", challenge), ("u", ama));
        Guid strength = await db.ScalarAsync<Guid>("select id from community.rubric_criterion where rubric_id = @r and code = 'c1'", ("r", rubric));
        Guid neatness = await db.ScalarAsync<Guid>("select id from community.rubric_criterion where rubric_id = @r and code = 'c2'", ("r", rubric));

        // Only one criterion scored: refused, nothing kept.
        string challengePage = $"{spacePage}/challenges/{challenge:D}";
        (await staffBrowser.GetAsync(challengePage)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staffBrowser.Html.ShouldNotContain("provider.challenges.");
        string form = "feedback-" + work.ToString("N");
        HttpResponseMessage partial = await staffBrowser.PostAsync(challengePage, form, ("comment", "Strong."), ("outcome", "accepted"),
            ("score-" + strength.ToString("N"), "3"), ("score-" + neatness.ToString("N"), ""));
        partial.StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(staffBrowser.Html).ShouldContain("Score every criterion from 0 to 4, or none.");
        (await db.ScalarAsync<long>("select count(*) from community.feedback where submission_id = @s", ("s", work))).ShouldBe(0);

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().GiveFeedbackAsync(new UserId(staff), space, challenge, work, "Strong.", "accepted",
                new Dictionary<Guid, int> { [strength] = 5, [neatness] = 2 }, Ct)).Error!.Code.ShouldBe("challenges.scores");
        }

        // Every criterion: accepted, 3 and 2, so (3×2 + 2) of (4×2 + 4), 67%.
        await staffBrowser.GetAsync(challengePage);
        (await staffBrowser.PostAsync(challengePage, form, ("comment", "Strong, a bit untidy."), ("outcome", "accepted"),
                ("score-" + strength.ToString("N"), "3"), ("score-" + neatness.ToString("N"), "2")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{challengePage}?done=accepted");
        await staffBrowser.GetAsync(challengePage);
        Shown(staffBrowser.Html).ShouldContain("Overall: 67%");

        // A comment alone still goes, with no scores.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Challenges>().GiveFeedbackAsync(new UserId(staff), space, challenge, work, "Well done again.", null, null, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, ama))
        {
            IReadOnlyList<FeedbackView> feedback = (await scope.ServiceProvider.GetRequiredService<Challenges>().GetAsync(new UserId(ama), space, challenge, Ct)).Value.Mine!.Feedback;
            feedback[0].Scores.Select(s => (s.Label, s.Score, s.ScaleMax)).ShouldBe([("Strength", 3, 4), ("Neatness", 2, 4)]);
            feedback[0].Percent.ShouldBe(67);
            (feedback[1].Scores.Count, feedback[1].Percent).ShouldBe((0, (int?)null));
        }

        // Scored with, it's fixed: no change, no edit link; archiving still works, and the challenge keeps it.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            Rubrics service = scope.ServiceProvider.GetRequiredService<Rubrics>();
            (await service.SaveAsync(new UserId(owner), org, rubric, "Changed", 4, [new("Strength", null, null)], Ct)).Error!.Code.ShouldBe("rubrics.in_use");
            (await service.GetAsync(org, rubric, Ct))!.InUse.ShouldBeTrue();
            (await service.ArchiveAsync(org, rubric, archived: true, Ct)).IsSuccess.ShouldBeTrue();
        }

        await admin.GetAsync(rubrics);
        admin.Html.ShouldNotContain($"rubrics?edit={rubric}");
        (await db.ScalarAsync<Guid>("select rubric_id from community.challenge where id = @c", ("c", challenge))).ShouldBe(rubric);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'rubrics.saved' and @r = any(subject_ids)", ("r", rubric))).ShouldBe(1);
    }
}
