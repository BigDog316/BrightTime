using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Modules.Learning.Badges;
using BrightTime.Modules.Learning.Playlists;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's eligibility rules and waivers (D63): rules added, refused and removed,
/// tried against a made-up applicant; a waiver written, published, asked for on a listing, kept
/// from retiring while in use and revised; and only owners and admins allowed.
/// </summary>
/// <remarks>
/// The provider is the fifth approved organisation from sorted index 52 on with a published
/// builtin listing (the applications, attendance, payments and forms tests take the first four);
/// the stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed partial class EligibilityWaiverPagesTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private (SeedOrganisation Provider, Guid Listing, SeedOrganisation Stranger) Arrange()
    {
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(4).First();
        return (provider, db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id, sorted[50]);
    }

    [Fact]
    public async Task Admins_set_and_try_an_activitys_eligibility_rules()
    {
        RequireDatabase();
        (SeedOrganisation provider, Guid listing, SeedOrganisation stranger) = Arrange();
        string page = $"/provider/orgs/{provider.Id}/listings/{listing}/eligibility";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(AdminOf(provider));
        (await admin.GetAsync($"/provider/orgs/{provider.Id}/forms")).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain($"href=\"{page}\"");
        (await admin.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("No rules: anyone can apply.");
        string neighbourhood = NeighbourhoodChoice().Match(admin.Html).Groups[1].Value;

        // An age rule that must be met, and an advisory neighbourhood rule; a backwards range is refused.
        (await admin.PostAsync(page, "add", ("kind", "age_at_start"), ("min", "9"), ("max", "12"), ("on", "2027-07-01"), ("hard", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(page);
        (await admin.PostAsync(page, "add", ("kind", "neighbourhood_in"), ("min", ""), ("max", ""), ("on", ""), ("neighbourhood", neighbourhood))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(page);
        admin.Html.ShouldContain("Age at the start: 9 to 12");
        admin.Html.ShouldContain("(advice only)");
        (await admin.PostAsync(page, "add", ("kind", "grade_range"), ("min", "8"), ("max", "3"), ("on", ""), ("hard", "true"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Rule 3:");
        (await db.ScalarAsync<long>("select count(*) from enrollment.eligibility_rule where listing_id = @l", ("l", listing))).ShouldBe(2);

        // Tried against made-up applicants: eleven on the day fits, sixteen doesn't; nothing is saved.
        (await admin.PostAsync(page, "try", ("dob", "2016-01-01"), ("grade", ""), ("neighbourhood", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Eligible: Yes");
        (await admin.PostAsync(page, "try", ("dob", "2011-01-01"), ("grade", ""), ("neighbourhood", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Eligible: No");

        // A rule removed.
        Guid rule = await db.ScalarAsync<Guid>("select id from enrollment.eligibility_rule where listing_id = @l and rule_kind = 'neighbourhood_in'", ("l", listing));
        (await admin.PostAsync(page, $"remove-{rule:N}", ("rule", rule.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<long>("select count(*) from enrollment.eligibility_rule where listing_id = @l", ("l", listing))).ShouldBe(1);

        using Browser other = db.NewBrowser();
        await other.SignInAsync(AdminOf(stranger));
        (await other.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admins_write_publish_and_ask_for_a_waiver_and_nobody_else_can()
    {
        RequireDatabase();
        (SeedOrganisation provider, Guid listing, SeedOrganisation stranger) = Arrange();
        string waivers = $"/provider/orgs/{provider.Id}/waivers";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(AdminOf(provider));
        (await admin.GetAsync(waivers)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Written as a draft, then changed.
        HttpResponseMessage created = await admin.PostAsync(waivers, "create", ("title", "Photo release"), ("body", "We may take photos."), ("guardian", "true"));
        created.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string editor = created.Headers.Location!.AbsolutePath;
        Guid waiver = Guid.Parse(editor[(editor.LastIndexOf('/') + 1)..]);
        await admin.GetAsync(created.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("Waiver created as a draft.");
        (await admin.PostAsync(editor, "text", ("body", "We may take **photos** at sessions."), ("guardian", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Published, its exact text shown and locked, then asked for on a listing.
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "publish")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(editor);
        admin.Html.ShouldContain("<strong>photos</strong>");
        admin.Html.ShouldContain("families sign exactly this text");
        (await admin.PostAsync(editor, "attach", ("listing", listing.ToString()), ("required", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<bool>("select is_required from enrollment.listing_waiver where listing_id = @l and waiver_id = @w", ("l", listing), ("w", waiver))).ShouldBeTrue();

        // In use, it can't be retired; its next version starts as a draft.
        await admin.GetAsync(editor);
        (await admin.PostAsync(editor, "retire")).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("A listing still asks families to sign this.");
        HttpResponseMessage revised = await admin.PostAsync(editor, "revise");
        revised.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(revised.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("This is the next version, as a draft.");
        admin.Html.ShouldContain("Version 2");

        using Browser other = db.NewBrowser();
        await other.SignInAsync(AdminOf(stranger));
        (await other.GetAsync(waivers)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.GetAsync(editor)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        other.Html.ShouldNotContain("Photo release");
    }

    [Fact]
    public async Task Admins_ask_for_a_badge_or_a_finished_playlist_and_try_it()
    {
        RequireDatabase();
        // The other test's activity: it may hold that test's age rule (9 to 12) or none, so the
        // made-up applicant is eleven, and only this test's own rules are removed at the end.
        (SeedOrganisation provider, Guid listing, _) = Arrange();
        string page = $"/provider/orgs/{provider.Id}/listings/{listing}/eligibility";
        Guid admin = SeedIds.For("user/" + AdminOf(provider));
        OrganisationId org = new(provider.Id);
        string suffix = Guid.NewGuid().ToString("N")[..6];
        Guid badge, playlist;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, admin))
        {
            Badges badges = scope.ServiceProvider.GetRequiredService<Badges>();
            badge = (await badges.CreateAsync(org, new Uri("https://kettle.localhost/"),
                new BadgeClassInput("Water Safety " + suffix, "Safe by the water.", "Passed the water safety check at the pool.", null), TestContext.Current.CancellationToken)).Value.Id;
            (await badges.PublishAsync(org, badge, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            Playlists playlists = scope.ServiceProvider.GetRequiredService<Playlists>();
            playlist = (await playlists.CreateAsync(org, new UserId(admin), new PlaylistInput("Pool Ready " + suffix, null, true, null, null, null, null, null, null), TestContext.Current.CancellationToken)).Value.Id;
            (await playlists.SetStepsAsync(org, playlist, [new StepInput("reflection", "Why water safety matters", null, null, null, null, true, false)], TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            (await playlists.PublishAsync(org, playlist, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        }

        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(AdminOf(provider));
        (await browser.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(browser.Html).Replace("\u2068", "", StringComparison.Ordinal).Replace("\u2069", "", StringComparison.Ordinal);
        html.ShouldContain($"value=\"{badge}\"");
        html.ShouldContain($"Water Safety {suffix}, from {provider.DisplayName}");
        html.ShouldContain($"Pool Ready {suffix}, from {provider.DisplayName}");
        html.ShouldNotContain("try-achieved", Case.Sensitive, "nothing to hold yet");
        html.ShouldNotContain("provider.eligibility.");

        // A badge rule needs its badge; with one it must be met. A playlist rule is advice only.
        (await browser.PostAsync(page, "add", ("kind", "badge_required"), ("badge", ""), ("playlist", ""), ("min", ""), ("max", ""), ("on", ""), ("hard", "true"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Rule ");
        await browser.GetAsync(page);
        (await browser.PostAsync(page, "add", ("kind", "badge_required"), ("badge", badge.ToString()), ("playlist", ""), ("min", ""), ("max", ""), ("on", ""), ("hard", "true")))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await browser.GetAsync(page);
        (await browser.PostAsync(page, "add", ("kind", "playlist_completed"), ("badge", ""), ("playlist", playlist.ToString()), ("min", ""), ("max", ""), ("on", "")))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await browser.GetAsync(page);
        html = WebUtility.HtmlDecode(browser.Html);
        html.ShouldContain($"Holds a badge: Water Safety {suffix} (must be met)");
        html.ShouldContain($"Completed a playlist: Pool Ready {suffix} (advice only)");

        // Tried: without them the applicant isn't eligible; holding them, they are.
        (await browser.PostAsync(page, "try", ("dob", "2016-01-01"), ("grade", ""), ("neighbourhood", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Eligible: No");
        (await browser.PostAsync(page, "try", ("dob", "2016-01-01"), ("grade", ""), ("neighbourhood", ""), ("achieved", "true"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Eligible: Yes");

        // Both removed, so the activity is left as the other test expects. Saving the rules gives
        // each a new id, so each removal reads the next one afresh.
        const string Ours = "listing_id = @l and rule_kind in ('badge_required', 'playlist_completed')";
        for (int i = 0; i < 2; i++)
        {
            Guid rule = await db.ScalarAsync<Guid>($"select id from enrollment.eligibility_rule where {Ours} limit 1", ("l", listing));
            await browser.GetAsync(page);
            (await browser.PostAsync(page, $"remove-{rule:N}", ("rule", rule.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        (await db.ScalarAsync<long>($"select count(*) from enrollment.eligibility_rule where {Ours}", ("l", listing))).ShouldBe(0);
    }

    [GeneratedRegex("name=\"neighbourhood\" value=\"([0-9a-f-]{36})\"")]
    private static partial Regex NeighbourhoodChoice();
}
