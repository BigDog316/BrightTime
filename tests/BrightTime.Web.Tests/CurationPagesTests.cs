using System.Net;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Web.Tests;

/// <summary>
/// The organiser's collections (D94): begun as a draft (a bad link name refused), not published
/// while empty, published listings found by title and added, noted, moved and removed, worded in
/// another language, featured and published to the site and the home page, its link name fixed
/// from then on, and archived off the site. A provider's admin never gets in.
/// </summary>
/// <remarks>Makes its own collection, so the seeded ones the finder tests read stay as they are.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class CurationPagesTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task An_organiser_curates_a_collection_from_draft_to_the_site_and_off_it()
    {
        RequireDatabase();
        SeedListing[] picks = [.. db.Data.Listings.Where(l => l.Status == "published").GroupBy(l => l.Title, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).Select(g => g.Single()).OrderBy(l => l.Title, StringComparer.Ordinal).Take(2)];
        (SeedListing first, SeedListing second) = (picks[0], picks[1]);
        string suffix = Guid.NewGuid().ToString("N")[..6];
        string title = "Rainy Day Picks " + suffix;
        string slug = "rainy-day-" + suffix;

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        (await organiser.GetAsync("/organiser/collections")).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("href=\"/organiser/collections\"", Case.Sensitive, "the console's nav links to it");
        organiser.Html.ShouldNotContain("curation.");

        // Begun as a draft; a link name with spaces is refused, with the title kept.
        (await organiser.PostAsync("/organiser/collections", "create", ("title", title), ("slug", "Rainy Day"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Use 3 to 60 lower-case letters, numbers and hyphens for the link name.");
        organiser.Html.ShouldContain($"value=\"{title}\"");
        await organiser.GetAsync("/organiser/collections");
        HttpResponseMessage created = await organiser.PostAsync("/organiser/collections", "create", ("title", title), ("slug", slug));
        created.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string page = created.Headers.Location!.AbsolutePath;
        created.Headers.Location.Query.ShouldBe("?done=created");
        Guid id = Guid.Parse(page[(page.LastIndexOf('/') + 1)..]);

        // Not published while it's empty.
        await organiser.GetAsync(page);
        organiser.Html.ShouldNotContain("curation.");
        (await organiser.PostAsync(page, "publish")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Add an activity before publishing the collection.");

        // Published listings found by title and added, at the end.
        foreach (SeedListing l in picks)
        {
            string find = $"{page}?find={Uri.EscapeDataString(l.Title)}";
            await organiser.GetAsync(find);
            Shown(organiser.Html).ShouldContain(l.Title);
            (await organiser.PostAsync(find, "add-" + l.Id.ToString("N"))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=listings");
        }

        await organiser.GetAsync($"{page}?find={Uri.EscapeDataString(first.Title)}");
        organiser.Html.ShouldNotContain("add-" + first.Id.ToString("N"), Case.Sensitive, "one already in isn't offered again");

        // A note on the second, then it's moved to the top.
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "note-1", ("note", "Good when it rains."))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=listings");
        await organiser.GetAsync(page);
        await organiser.PostAsync(page, "up-1");

        // Its details: a last day before the first is refused; then featured, at place 5, in Spanish too.
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "details", ("title", title), ("description", "Indoor things to do."), ("slug", slug), ("starts", "2026-06-10"), ("ends", "2026-06-01"), ("order", "5")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("The last day can't be before the first.");
        organiser.Html.ShouldContain("value=\"2026-06-10\"", Case.Sensitive, "what was typed stays");
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "details", ("title", title), ("description", "Indoor things to do."), ("slug", slug), ("starts", ""), ("ends", ""), ("order", "5"), ("featured", "true")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=saved");
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "wording-es", ("wordingTitle", "Para días de lluvia"), ("wordingDescription", "Cosas para hacer bajo techo.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=worded");
        (await db.ScalarAsync<string>("select title from opportunity.collection_translation where collection_id = @c and locale = 'es'", ("c", id))).ShouldBe("Para días de lluvia");

        // Published: on the site in the organiser's order with the note, and on the home page.
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "publish")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=published");
        using Browser anyone = db.NewBrowser();
        (await anyone.GetAsync($"/collections/{slug}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        string shown = Shown(anyone.Html);
        shown.ShouldContain(title);
        shown.ShouldContain("Good when it rains.");
        shown.IndexOf(second.Title, StringComparison.Ordinal).ShouldBeLessThan(shown.IndexOf(first.Title, StringComparison.Ordinal), "moved to the top");
        await anyone.GetAsync("/");
        anyone.Html.ShouldContain($"href=\"/collections/{slug}\"", Case.Sensitive, "featured on the home page");

        // From now on the link name stays; the page shows it fixed.
        await organiser.GetAsync(page);
        Shown(organiser.Html).ShouldContain("The link name stays the same once a collection has been published");
        (await organiser.PostAsync(page, "details", ("title", title), ("description", ""), ("slug", slug + "-new"), ("starts", ""), ("ends", ""), ("order", "5"), ("featured", "true")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("The link name stays the same once a collection has been published.");

        // One taken out; then archived, it's off the site and marked in the list.
        await organiser.GetAsync(page);
        await organiser.PostAsync(page, "remove-0");
        (await db.ScalarAsync<long>("select count(*) from opportunity.collection_item where collection_id = @c", ("c", id))).ShouldBe(1);
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "archive")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=archived");
        (await anyone.GetAsync($"/collections/{slug}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await organiser.GetAsync("/organiser/collections");
        organiser.Html.ShouldContain(title);
        organiser.Html.ShouldContain("Archived");

        foreach (string action in new[] { "curation.collection_created", "curation.collection_changed", "curation.collection_published", "curation.collection_archived" })
        {
            (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and subject_ids = array[@c]", ("a", action), ("c", id)))
                .ShouldBeGreaterThan(0, action);
        }

        // A provider's admin is turned away.
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync($"admin@{db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).First().Slug}.example.org");
        HttpResponseMessage refused = await provider.GetAsync(page);
        (refused.StatusCode == HttpStatusCode.Forbidden
         || (refused.StatusCode == HttpStatusCode.Redirect && refused.Headers.Location!.ToString().Contains("/access-denied", StringComparison.Ordinal))).ShouldBeTrue();
    }
}
