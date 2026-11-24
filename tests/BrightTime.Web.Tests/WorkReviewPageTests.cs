using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Learning.Badges;
using BrightTime.Modules.Learning.Playlists;
using BrightTime.Modules.Learning.Records;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Young people's work in the provider console (D82): a project about one of the organisation's
/// activities and a playlist step its staff check, each approved or sent back with a note, the
/// young person named only by their display name, and nobody outside the organisation let in.
/// </summary>
/// <remarks>
/// Every seed organisation is some web test's already. The provider is the messaging tests' (the
/// sixth approved organisation from sorted index 52 on with a published builtin listing): they
/// count only conversations and the listing's enrolled families, and the young person here is
/// never enrolled. They're an account made here, whose taking part is written straight to the
/// table: what's under test is the review, not attendance. The stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class WorkReviewPageTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private AsyncServiceScope As(Guid user)
    {
        AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        TenantContext tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(new TenantId(db.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
        tenant.SetActor(new UserId(user), ActorKind.User);
        return scope;
    }

    private static readonly Uri Site = new("https://kettle.localhost/");

    /// <summary>A young person who took part in the listing, can sign up for themselves, and shows as "Ama P".</summary>
    private async Task<Guid> YoungPersonAsync(SeedOrganisation org, Guid listing)
    {
        Guid user = UuidV7.NewId();
        string email = $"work-{user:N}@example.org";
        await db.ScalarAsync<int>(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email,
                                           security_stamp, concurrency_stamp, account_kind, age_band)
            values (@u, @t, @e, upper(@e), @e, upper(@e), 'stamp', 'concurrency', 'youth', '15_17');
            insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_self_enroll, recomputed_at)
            values (@u, @t, false, true, true, now());
            insert into learning.learner_profile (user_id, tenant_id, display_name, visibility) values (@u, @t, 'Ama P', 'private');
            insert into learning.participation_record (id, tenant_id, user_id, listing_id, org_id, title_snapshot, org_name_snapshot, started_on, sessions_attended, hours, source)
            values (@r, @t, @u, @l, @o, 'River Walks', @n, date '2026-09-01', 4, 6, 'attendance');
            select 1;
            """,
            ("u", user), ("t", db.Data.TenantId), ("e", email), ("r", UuidV7.NewId()), ("l", listing), ("o", org.Id), ("n", org.DisplayName));
        return user;
    }

    [Fact]
    public async Task Staff_approve_or_send_back_a_young_persons_project_and_playlist_step()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(5).First();
        Guid listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id;
        Guid admin = SeedIds.For("user/" + AdminOf(provider));
        OrganisationId org = new(provider.Id);
        Guid youth = await YoungPersonAsync(provider, listing);

        // The organisation's playlist: one check-in staff review, with a badge at the end, and
        // pictures of their work (D104), which staff check too but which isn't needed to finish.
        Guid playlist, step, upload;
        await using (AsyncServiceScope scope = As(admin))
        {
            Badges badges = scope.ServiceProvider.GetRequiredService<Badges>();
            Playlists playlists = scope.ServiceProvider.GetRequiredService<Playlists>();
            Guid badge = (await badges.CreateAsync(org, Site, new BadgeClassInput("River Talker", "Talked rivers with a ranger.", "Checked in with a ranger.", null), Ct)).Value.Id;
            (await badges.PublishAsync(org, badge, Ct)).IsSuccess.ShouldBeTrue();
            PlaylistView made = (await playlists.CreateAsync(org, new UserId(admin), new PlaylistInput("River Talk " + Guid.NewGuid().ToString("N")[..6], null, true, null, null, null, badge, null, null), Ct)).Value;
            PlaylistView withSteps = (await playlists.SetStepsAsync(org, made.Id,
                [new StepInput("upload_artifact", "Show your map", null, null, null, null, false, false),
                 new StepInput("mentor_checkin", "Check in with a ranger", null, null, null, null, true, true)], Ct)).Value;
            (await playlists.PublishAsync(org, made.Id, Ct)).IsSuccess.ShouldBeTrue();
            (playlist, step, upload) = (made.Id, withSteps.Steps[1].Id, withSteps.Steps[0].Id);
        }

        // The young person sends two projects, one with a picture, the check-in with what they told
        // the ranger, and the map as the evidence for the other step.
        Guid kept, returned, map;
        await using (AsyncServiceScope scope = As(youth))
        {
            Projects projects = scope.ServiceProvider.GetRequiredService<Projects>();
            Playlists playlists = scope.ServiceProvider.GetRequiredService<Playlists>();
            kept = (await projects.CreateAsync(new UserId(youth), new ProjectInput("Mussel map", "A map of the mussel beds.\nWith a key.", listing), Ct)).Value.Id;
            returned = (await projects.CreateAsync(new UserId(youth), new ProjectInput("River poem", null, listing), Ct)).Value.Id;
            map = (await projects.AddPictureAsync(new UserId(youth), kept, TestImages.Png(640, 480), "A hand-drawn map of the river", "With a key", Ct)).Value.Pictures.Single().MediaId;
            (await projects.SubmitAsync(new UserId(youth), kept, Ct)).IsSuccess.ShouldBeTrue();
            (await projects.SubmitAsync(new UserId(youth), returned, Ct)).IsSuccess.ShouldBeTrue();
            (await playlists.EnrollAsync(new UserId(youth), playlist, Ct)).IsSuccess.ShouldBeTrue();
            (await playlists.DoStepAsync(new UserId(youth), Site, playlist, step, "Told the ranger about the crayfish.", Ct)).IsSuccess.ShouldBeTrue();
            (await playlists.DoStepAsync(new UserId(youth), Site, playlist, upload, null, null, kept, Ct)).IsSuccess.ShouldBeTrue();
        }

        Guid enrollment = await db.ScalarAsync<Guid>("select id from learning.playlist_enrollment where playlist_id = @p and user_id = @u", ("p", playlist), ("u", youth));
        string work = $"/provider/orgs/{provider.Id}/work";
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));

        // Both projects and the step wait, named by the display name alone.
        (await staff.GetAsync(work)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(staff.Html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);
        html.ShouldContain("Mussel map");
        html.ShouldContain("River poem");
        html.ShouldContain("By Ama P · About River Walks");
        html.ShouldContain("A map of the mussel beds.\nWith a key.");
        html.ShouldContain("Check in with a ranger");
        html.ShouldContain("Told the ranger about the crayfish.");
        html.ShouldContain($"href=\"/provider/orgs/{provider.Id}/work\" aria-current=\"page\"", Case.Sensitive, "the tab shows, current");
        html.ShouldNotContain(youth.ToString(), Case.Sensitive, "nothing of the account");
        html.ShouldNotContain("provider.work.");

        // The picture shows on the project and as the step's evidence, fetched for staff alone.
        string picture = $"/provider/orgs/{provider.Id}/work/pictures/{map}";
        html.Split($"<img src=\"{picture}\" alt=\"A hand-drawn map of the river\"").Length.ShouldBe(3, "once on the project, once as the evidence");
        html.ShouldContain("What they sent: <span dir=\"auto\">Mussel map</span>");
        html.ShouldContain("With a key");
        HttpResponseMessage fetched = await staff.GetAsync(picture);
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(TestImages.Png(640, 480));
        fetched.Headers.CacheControl!.NoStore.ShouldBeTrue();
        await staff.GetAsync(work);

        // Approved, a project leaves the list.
        HttpResponseMessage approved = await staff.PostAsync(work, "project-" + kept.ToString("N"), ("project", kept.ToString()), ("decision", "approve"), ("note", ""));
        approved.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        approved.Headers.Location!.PathAndQuery.ShouldBe($"{work}?done=approved");
        (await db.ScalarAsync<string>("select status from learning.project where id = @p", ("p", kept))).ShouldBe("approved");

        // Sent back, it needs a note; with one, the young person has it.
        await staff.GetAsync(work);
        (await staff.PostAsync(work, "project-" + returned.ToString("N"), ("project", returned.ToString()), ("decision", "send_back"), ("note", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Say what to change, in up to 1,000 characters.");
        HttpResponseMessage sentBack = await staff.PostAsync(work, "project-" + returned.ToString("N"), ("project", returned.ToString()), ("decision", "send_back"), ("note", "Add a verse about the heron."));
        sentBack.Headers.Location!.PathAndQuery.ShouldBe($"{work}?done=sent_back");
        (await db.ScalarAsync<string>("select status || ':' || review_note from learning.project where id = @p", ("p", returned))).ShouldBe("draft:Add a verse about the heron.");

        // The approved project's picture is still the waiting step's evidence; checked, it's neither.
        (await staff.GetAsync(picture)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await staff.GetAsync(work);
        (await staff.PostAsync(work, "step-" + enrollment.ToString("N") + "-" + upload.ToString("N"),
            ("playlist", playlist.ToString()), ("step", upload.ToString()), ("enrollment", enrollment.ToString()), ("decision", "approve"), ("note", ""))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await staff.GetAsync(picture)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The step approved completes the playlist, and its badge goes to the young person.
        await staff.GetAsync(work);
        HttpResponseMessage stepped = await staff.PostAsync(work, "step-" + enrollment.ToString("N") + "-" + step.ToString("N"),
            ("playlist", playlist.ToString()), ("step", step.ToString()), ("enrollment", enrollment.ToString()), ("decision", "approve"), ("note", ""));
        stepped.Headers.Location!.PathAndQuery.ShouldBe($"{work}?done=approved");
        (await db.ScalarAsync<bool>("select completed_at is not null and awarded_assertion_id is not null from learning.playlist_enrollment where id = @e", ("e", enrollment))).ShouldBeTrue();
        await staff.GetAsync(stepped.Headers.Location!.PathAndQuery);
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Approved.");
        html.ShouldContain("No projects are waiting.");
        html.ShouldContain("No playlist steps are waiting.");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.project.reviewed' and actor_org_id = @o", ("o", provider.Id))).ShouldBe(2);

        // The young person is told each time (D83): the badge with the playlist finished, and nothing they wrote.
        string told = await db.ScalarAsync<string>(
            """
            select string_agg(substring(payload::text from 'notify\.[a-z_]+\.[a-z_]+'), ',' order by id)
            from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u
            """,
            ("u", "%" + youth + "%"));
        told.ShouldBe("notify.project.approved,notify.project.sent_back,notify.step.approved,notify.badge.awarded,notify.playlist.completed");
        string payloads = await db.ScalarAsync<string>(
            "select string_agg(payload::text, ' ') from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u",
            ("u", "%" + youth + "%"));
        payloads.ShouldContain("/portfolio/projects/" + kept.ToString("D"));
        payloads.ShouldContain(provider.DisplayName);
        payloads.ShouldNotContain("Mussel map");
        payloads.ShouldNotContain("crayfish");

        // Another organisation's admin is turned away.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(work)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("Mussel map");
        (await stranger.GetAsync(picture)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
