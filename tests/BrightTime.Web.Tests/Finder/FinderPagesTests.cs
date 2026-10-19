using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Web.Tests.Finder;

/// <summary>
/// The finder's pages against the seeded county: organisations, collections, neighbourhoods,
/// the map, the home page, and saving.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed partial class FinderPagesTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private async Task<string> PageAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using Browser browser = db.NewBrowser();
        HttpResponseMessage response = await browser.GetAsync(path);
        response.StatusCode.ShouldBe(expected, path);
        return browser.Html;
    }

    private SeedOrganisation ApprovedWithListings() =>
        db.Data.Organisations.First(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published"));

    // ---- organisations ----------------------------------------------------------------

    [Fact]
    public async Task An_approved_organisation_has_a_page_with_its_mission_and_listings()
    {
        RequireDatabase();
        SeedOrganisation org = ApprovedWithListings();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == org.Id && l.Status == "published");

        string html = await PageAsync($"/organisations/{org.Slug}");

        html.ShouldContain($">{org.DisplayName}</h1>");
        html.ShouldContain(org.Mission);
        html.ShouldContain($"href=\"/listings/{listing.Slug}\"");
    }

    [Fact]
    public async Task An_organisation_that_is_not_approved_answers_as_if_it_did_not_exist()
    {
        RequireDatabase();
        SeedOrganisation applying = db.Data.Organisations.First(o => o.Status != "approved");

        await PageAsync($"/organisations/{applying.Slug}", HttpStatusCode.NotFound);
        await PageAsync("/organisations/no-such-organisation", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_private_site_shows_its_neighbourhood_and_never_its_street()
    {
        RequireDatabase();
        SeedSite hidden = db.Data.Sites.First(s => !s.IsAddressPublic && db.Data.Organisations.Any(o => o.Id == s.OrgId && o.Status == "approved"));
        SeedOrganisation org = db.Data.Organisations.First(o => o.Id == hidden.OrgId);

        string html = await PageAsync($"/organisations/{org.Slug}");

        html.ShouldContain(hidden.Name);
        html.ShouldNotContain(hidden.AddressLine1);
        html.ShouldContain("The exact address is shared once you register.");
    }

    // ---- collections ------------------------------------------------------------------

    [Fact]
    public async Task A_collection_lists_its_listings_in_the_curators_order_with_notes()
    {
        RequireDatabase();
        SeedCollection free = db.Data.Collections!.First(c => c.Slug == KettleCuration.FreeCollection);
        string[] slugs = [.. free.Items.Select(i => db.Data.Listings.First(l => l.Id == i.ListingId).Slug)];

        string html = await PageAsync($"/collections/{free.Slug}");

        html.ShouldContain(">Free and fun</h1>");
        html.ShouldContain("A favourite with first-timers.");
        int[] positions = [.. slugs.Select(s => html.IndexOf($"href=\"/listings/{s}\"", StringComparison.Ordinal))];
        positions.ShouldAllBe(p => p >= 0);
        positions.ShouldBeInOrder(SortDirection.Ascending);
    }

    [Fact]
    public async Task A_collection_is_shown_in_the_readers_language_where_it_was_translated()
    {
        RequireDatabase();

        (await PageAsync($"/collections/{KettleCuration.FreeCollection}?lang=es")).ShouldContain(">Gratis y divertido</h1>");
        (await PageAsync($"/collections/{KettleCuration.FreeCollection}?lang=bn"))
            .ShouldContain("<h1 dir=\"auto\" lang=\"en\">Free and fun</h1>", Case.Sensitive, "untranslated, so marked as English");
    }

    [Fact]
    public async Task A_listing_that_is_not_published_never_shows_in_a_collection()
    {
        RequireDatabase();
        SeedListing published = db.Data.Listings.First(l => l.Status == "published");
        SeedListing draft = db.Data.Listings.First(l => l.Status == "draft");
        Guid collection = Guid.CreateVersion7();
        await db.ScalarAsync<int>(
            """
            with c as (insert into opportunity.collection (id, tenant_id, slug, title, status) values (@id, @t, 'test-hidden-draft', 'Hidden draft test', 'published') returning id),
                 i as (insert into opportunity.collection_item (collection_id, listing_id, sort_order) select id, @a, 0 from c union all select id, @b, 1 from c returning 1)
            select count(*)::int from i
            """, ("id", collection), ("t", KettleSeed.TenantId), ("a", draft.Id), ("b", published.Id));

        string html = await PageAsync("/collections/test-hidden-draft");

        html.ShouldContain($"href=\"/listings/{published.Slug}\"");
        html.ShouldNotContain(draft.Slug);
    }

    [Fact]
    public async Task The_home_page_shows_featured_collections_this_week_and_neighbourhoods()
    {
        RequireDatabase();

        string html = await PageAsync("/");

        html.ShouldContain("href=\"/collections/free-and-fun\"");
        html.ShouldContain("href=\"/collections/first-jobs\"");
        html.ShouldNotContain("href=\"/collections/arts-after-school\"", Case.Sensitive, "not featured");
        html.ShouldContain("href=\"/neighbourhoods/hood-downtown\"");
        html.ShouldContain("On this week");
    }

    // ---- map and neighbourhood filter ---------------------------------------------------

    [Fact]
    public async Task The_map_draws_every_neighbourhood_as_a_link_and_leaks_no_coordinate()
    {
        RequireDatabase();

        string html = await PageAsync("/search?view=map");

        PathElement().Count(html).ShouldBe(18);
        html.ShouldContain("href=\"/search?hood=hood-westside\"");
        html.ShouldContain("<ul class=\"hood-list\">");

        // Projected drawing units only: no longitude or latitude of any site or area appears.
        html.ShouldNotContain("-73.");
        html.ShouldNotContain("41.0");
        foreach (SeedSite site in db.Data.Sites.Where(s => !s.IsAddressPublic))
        {
            html.ShouldNotContain(site.Lon.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task Filtering_by_neighbourhood_narrows_the_list_and_says_where()
    {
        RequireDatabase();

        string html = await PageAsync("/search?hood=hood-downtown");

        html.ShouldMatch("id=\"results-heading\">[^<]* in Downtown</h2>");
        html.ShouldContain("<option value=\"hood-downtown\" selected>");

        (await PageAsync("/search?hood=hood-atlantis")).ShouldContain("We don&#x27;t know that neighbourhood");
    }

    [Fact]
    public async Task Filtering_by_organisation_shows_a_chip_to_remove_it()
    {
        RequireDatabase();
        SeedOrganisation org = ApprovedWithListings();

        string html = await PageAsync($"/search?org={org.Slug}");

        html.ShouldContain("class=\"filter-chip\"");
        html.ShouldContain(org.DisplayName);
        html.ShouldContain($"<input type=\"hidden\" name=\"org\" value=\"{org.Slug}\"");
    }

    // ---- neighbourhood pages -------------------------------------------------------------

    [Fact]
    public async Task A_neighbourhood_page_renders_the_organisers_blocks_safely()
    {
        RequireDatabase();

        string html = await PageAsync("/neighbourhoods/hood-downtown");

        html.ShouldContain(">About Downtown</h2>");
        html.ShouldContain("<strong>New here?</strong>");
        html.ShouldContain("<a href=\"/collections\">all collections</a>");
        html.ShouldContain("<details>");
        html.ShouldContain("Downtown youth outreach");
        html.ShouldContain("Programs starting soon");
        html.ShouldContain("href=\"/collections/free-and-fun\"");
        html.ShouldContain("aria-current=\"location\"", Case.Sensitive, "the small map marks where this is");
    }

    [Fact]
    public async Task A_neighbourhood_page_is_in_the_readers_language_when_written_and_english_otherwise()
    {
        RequireDatabase();

        (await PageAsync("/neighbourhoods/hood-downtown?lang=es")).ShouldContain(">Sobre Downtown</h2>");
        (await PageAsync("/neighbourhoods/hood-downtown?lang=bn")).ShouldContain("<div class=\"hood-blocks\" lang=\"en\">");
    }

    [Fact]
    public async Task A_neighbourhood_without_a_page_still_shows_what_is_there_and_an_unknown_one_is_a_404()
    {
        RequireDatabase();

        string html = await PageAsync("/neighbourhoods/hood-lakeview");

        html.ShouldContain(">Lakeview</h1>");
        html.ShouldNotContain("hood-blocks");
        html.ShouldContain("href=\"/search?hood=hood-lakeview\"");
        await PageAsync("/neighbourhoods/hood-atlantis", HttpStatusCode.NotFound);
    }

    // ---- saving ---------------------------------------------------------------------------

    [Fact]
    public async Task Saving_works_without_an_account_and_reminds_through_the_calendar()
    {
        RequireDatabase();
        SeedListing listing = db.Data.Listings.First(l => l.Status == "published" && l.Kind == "program");
        using Browser browser = db.NewBrowser();
        await browser.GetAsync($"/listings/{listing.Slug}");
        browser.Html.ShouldContain("aria-pressed=\"false\">Save</button>");

        HttpResponseMessage saved = await browser.PostAsync("/saved/change", null, ("listing", listing.Id.ToString()), ("action", "save"), ("returnUrl", $"/listings/{listing.Slug}"));
        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        saved.Headers.Location!.ToString().ShouldEndWith($"/listings/{listing.Slug}");

        await browser.GetAsync($"/listings/{listing.Slug}");
        browser.Html.ShouldContain("aria-pressed=\"true\">Saved</button>");

        await browser.GetAsync("/saved");
        browser.Html.ShouldContain($"href=\"/listings/{listing.Slug}\"");
        browser.Html.ShouldContain("Anyone who uses this browser can see them.");

        HttpResponseMessage ics = await browser.GetAsync("/saved/calendar.ics");
        ics.Content.Headers.ContentType!.MediaType.ShouldBe("text/calendar");
        ics.Headers.CacheControl!.NoStore.ShouldBeTrue();
        browser.Html.ShouldContain("BEGIN:VALARM");

        await browser.GetAsync("/saved");
        await browser.PostAsync("/saved/change", null, ("action", "clear"), ("returnUrl", "/saved"));
        await browser.GetAsync("/saved");
        browser.Html.ShouldContain("You haven&#x27;t saved anything yet.");
    }

    [Fact]
    public async Task Saving_comes_back_only_to_this_site_and_refuses_a_forged_form()
    {
        RequireDatabase();
        SeedListing listing = db.Data.Listings.First(l => l.Status == "published");
        using Browser browser = db.NewBrowser();
        await browser.GetAsync($"/listings/{listing.Slug}");

        HttpResponseMessage elsewhere = await browser.PostAsync("/saved/change", null, ("listing", listing.Id.ToString()), ("action", "save"), ("returnUrl", "https://evil.example/"));
        elsewhere.Headers.Location!.ToString().ShouldEndWith("/saved");

        using HttpClient bare = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false));
        using FormUrlEncodedContent forged = new([new("listing", listing.Id.ToString()), new("action", "save")]);
        (await bare.PostAsync(new Uri("/saved/change", UriKind.Relative), forged, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // The drawn areas: each also has a clipPath copy of its outline, which carries no class.
    [GeneratedRegex("<path d=\"M[^\"]*\" class=\"hood")]
    private static partial Regex PathElement();
}
