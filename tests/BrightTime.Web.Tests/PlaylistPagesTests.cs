using System.Net;
using BrightTime.Modules.Learning.Badges;
using BrightTime.Modules.Learning.Playlists;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Writing a playlist in the provider console (D86): begun as a draft, its details saved (bad
/// numbers and ranges refused, what was typed kept), its steps added, changed, moved and removed,
/// published (after which it can't change) and archived; and nobody outside the organisation gets in.
/// </summary>
/// <remarks>
/// The provider is the messaging tests' (see <see cref="YouthEnrolment.SecondListing"/>), whose
/// playlists other tests make too: this one keeps to its own. The stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class PlaylistPagesTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    [Fact]
    public async Task Staff_write_a_playlist_step_by_step_then_publish_and_archive_it()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        (SeedOrganisation provider, SeedListing listing) = YouthEnrolment.SecondListing(db);
        Guid admin = SeedIds.For("user/" + AdminOf(provider));
        OrganisationId org = new(provider.Id);
        Guid badge;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, admin))
        {
            Badges badges = scope.ServiceProvider.GetRequiredService<Badges>();
            badge = (await badges.CreateAsync(org, new Uri("https://kettle.localhost/"), new BadgeClassInput("Shore Walker", "Walked the shore.", "Walked the whole shore path and wrote about it.", null), Ct)).Value.Id;
            (await badges.PublishAsync(org, badge, Ct)).IsSuccess.ShouldBeTrue();
        }

        string playlists = $"/provider/orgs/{provider.Id}/playlists";
        string title = "Shore Walk " + Guid.NewGuid().ToString("N")[..6];
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));
        (await staff.GetAsync(playlists)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain($"href=\"{playlists}\" aria-current=\"page\"", Case.Sensitive, "the tab shows, current");
        staff.Html.ShouldNotContain("provider.playlists.");

        // Begun as a draft.
        HttpResponseMessage created = await staff.PostAsync(playlists, "create", ("title", title), ("description", "Walk the shore, and think about it."));
        created.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string page = created.Headers.Location!.AbsolutePath;
        Guid id = Guid.Parse(page[(page.LastIndexOf('/') + 1)..]);

        // Details: a word for a number, and an age out of range, are refused with what was typed kept.
        await staff.GetAsync(page);
        staff.Html.ShouldNotContain("provider.playlists.");
        (await staff.PostAsync(page, "details", ("title", title), ("description", "Walk the shore."), ("ageMin", "twelve"), ("ageMax", ""), ("hours", ""), ("badge", ""), ("requires", ""), ("requiredSteps", "")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Use whole numbers for ages");
        html.ShouldContain("value=\"twelve\"");
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "details", ("title", title), ("description", "Walk the shore."), ("ageMin", "30"), ("ageMax", ""), ("hours", ""), ("badge", ""), ("requires", ""), ("requiredSteps", "")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Ages from 5 to 25");
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "details", ("title", title), ("description", "Walk the shore."), ("ageMin", "12"), ("ageMax", "17"), ("hours", "3.5"),
            ("badge", badge.ToString()), ("requires", ""), ("requiredSteps", ""))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=saved");

        // Steps: a reflection; an activity to attend, refused without the activity; a link, refused unless https.
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "step", ("index", ""), ("kind", "reflection"), ("stepTitle", "Write about the tide"), ("instructions", "What changed?"), ("listing", ""), ("link", ""), ("minutes", "20"), ("required", "true")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=steps");
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "step", ("index", ""), ("kind", "attend_listing"), ("stepTitle", "Come along"), ("instructions", ""), ("listing", ""), ("link", ""), ("minutes", ""), ("required", "true")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        html = WebUtility.HtmlDecode(staff.Html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);
        html.ShouldContain("Step 2: choose a published listing to attend.");
        html.ShouldContain("value=\"Come along\"", Case.Sensitive, "what was typed stays");
        await staff.GetAsync(page);
        await staff.PostAsync(page, "step", ("index", ""), ("kind", "attend_listing"), ("stepTitle", "Come along"), ("instructions", ""), ("listing", listing.Id.ToString()), ("link", ""), ("minutes", ""), ("required", "true"));
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "step", ("index", ""), ("kind", "external_activity"), ("stepTitle", "Read the shore guide"), ("instructions", ""), ("listing", ""), ("link", "http://example.org/shore"), ("minutes", "")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal)
            .ShouldContain("Step 3: give a link starting with https://.");
        await staff.GetAsync(page);
        await staff.PostAsync(page, "step", ("index", ""), ("kind", "external_activity"), ("stepTitle", "Read the shore guide"), ("instructions", ""), ("listing", ""), ("link", "https://example.org/shore"), ("minutes", ""));

        // Moved up, changed, then one removed.
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "up-2")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=steps");
        await staff.GetAsync($"{page}?edit=1");
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Change step 1");
        await staff.PostAsync($"{page}?edit=1", "step", ("index", "1"), ("kind", "reflection"), ("stepTitle", "Write about the waves"), ("instructions", ""), ("listing", ""), ("link", ""), ("minutes", "15"), ("required", "true"));
        await staff.GetAsync(page);
        await staff.PostAsync(page, "remove-2");
        PlaylistView written;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, admin))
        {
            written = (await scope.ServiceProvider.GetRequiredService<Playlists>().GetAsync(org, id, Ct)).Value;
        }

        written.Steps.Select(s => (s.Kind, s.Title)).ShouldBe([("reflection", "Write about the waves"), ("external_activity", "Read the shore guide")]);
        (written.IsOrdered, written.AgeMin, written.AgeMax, written.EstimatedHours, written.CompletionBadgeClassId).ShouldBe((false, (short?)12, (short?)17, (decimal?)3.5m, (Guid?)badge));
        await staff.GetAsync(page);
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("An activity elsewhere · https://example.org/shore");
        html.ShouldContain("A reflection they write · About 15 minutes");

        // Published, it can't change; archived, nobody new starts it.
        (await staff.PostAsync(page, "publish")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=published");
        await staff.GetAsync($"{page}?done=published");
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Published. Young people can start it now.");
        html.ShouldContain("Shore Walker");
        html.ShouldContain("12 to 17");
        html.ShouldNotContain("formname=\"details\"", Case.Sensitive, "a published playlist has no form to change it");
        html.ShouldNotContain("formname=\"step\"");
        (await staff.PostAsync(page, "archive")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=archived");
        await staff.GetAsync(playlists);
        WebUtility.HtmlDecode(staff.Html).ShouldContain(title);

        // Another organisation's admin is turned away.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain(title);
    }
}
