using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Seed;

namespace BrightTime.Api.Tests;

/// <summary>
/// <c>/api/v1/public</c>, the app's read surface: the same catalogue, rules and privacy as the
/// web's finder, as JSON, with no account.
/// </summary>
[Collection(ApiDatabase.Name)]
public sealed class PublicApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private int PublishedCount => api.Data.Listings.Count(l =>
        l.Status == "published" && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved");

    private async Task<(HttpResponseMessage Response, JsonElement Body)> GetAsync(string path, string? acceptLanguage = null)
    {
        using HttpClient client = api.Client();
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        JsonElement body = response.Content.Headers.ContentLength == 0
            ? default
            : await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (response, body);
    }

    [Fact]
    public async Task Paging_through_search_returns_every_published_listing_once()
    {
        RequireDatabase();
        HashSet<Guid> seen = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            (HttpResponseMessage response, JsonElement body) = await GetAsync("api/v1/public/listings?limit=50" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            foreach (JsonElement item in body.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("id").GetGuid()).ShouldBeTrue("a listing appeared on two pages");
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null && ++pages < 50);

        seen.Count.ShouldBe(PublishedCount);
    }

    [Fact]
    public async Task A_result_carries_what_a_card_needs_and_why_it_matched()
    {
        RequireDatabase();
        (HttpResponseMessage response, JsonElement body) = await GetAsync("api/v1/public/listings?q=soccer");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLanguage.ShouldContain("en");
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.Vary.ShouldContain("Accept-Language");

        JsonElement first = body.GetProperty("items")[0];
        first.GetProperty("slug").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("kind").GetString().ShouldBeOneOf("program", "event", "job", "resource");
        first.GetProperty("organisation").GetProperty("name").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("cost").GetProperty("isFree").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        // The explainable ranking: catalogue keys, which the app renders in its own language.
        first.GetProperty("why").EnumerateArray().ShouldAllBe(w => w.GetProperty("key").GetString()!.Contains('.'));
    }

    [Fact]
    public async Task Filters_narrow_the_results()
    {
        RequireDatabase();

        JsonElement jobs = (await GetAsync("api/v1/public/listings?kind=job&limit=100")).Body.GetProperty("items");
        jobs.GetArrayLength().ShouldBeGreaterThan(0);
        jobs.EnumerateArray().ShouldAllBe(i => i.GetProperty("kind").GetString() == "job");

        JsonElement free = (await GetAsync("api/v1/public/listings?free=true&limit=100")).Body.GetProperty("items");
        free.EnumerateArray().ShouldAllBe(i => i.GetProperty("cost").GetProperty("isFree").GetBoolean());

        (await GetAsync("api/v1/public/listings?limit=5")).Body.GetProperty("items").GetArrayLength().ShouldBe(5);

        // A neighbourhood's results are as many as its page says it has.
        JsonElement downtown = (await GetAsync("api/v1/public/neighbourhoods/hood-downtown")).Body;
        int expected = downtown.GetProperty("listingCount").GetInt32();
        JsonElement near = (await GetAsync("api/v1/public/listings?nearNeighbourhood=hood-downtown&limit=100")).Body.GetProperty("items");
        near.GetArrayLength().ShouldBe(expected);

        // Category codes are the taxonomy's; several match any of them.
        int stem = (await GetAsync("api/v1/public/listings?category=stem&limit=100")).Body.GetProperty("items").GetArrayLength();
        int stemOrArts = (await GetAsync("api/v1/public/listings?category=stem&category=arts&limit=100")).Body.GetProperty("items").GetArrayLength();
        stem.ShouldBeGreaterThan(0);
        stemOrArts.ShouldBeGreaterThan(stem);
    }

    [Theory]
    [InlineData("age=99", "age")]
    [InlineData("limit=0", "limit")]
    [InlineData("free=maybe", "free")]
    [InlineData("from=2026-11-01", "from")]
    [InlineData("nearNeighbourhood=nowhere", "nearNeighbourhood")]
    [InlineData("category=Not%20A%20Code", "category")]
    [InlineData("sort=cheapest", "sort")]
    public async Task A_bad_value_is_a_validation_problem_naming_the_parameter(string query, string parameter)
    {
        RequireDatabase();
        (HttpResponseMessage response, JsonElement body) = await GetAsync("api/v1/public/listings?" + query);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        body.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:validation");
        body.GetProperty("errors").TryGetProperty(parameter, out _).ShouldBeTrue();
        body.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_kind_or_a_stale_cursor_is_dropped_and_said_so()
    {
        RequireDatabase();
        (HttpResponseMessage response, JsonElement body) = await GetAsync("api/v1/public/listings?kind=spaceship&cursor=not-a-cursor");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("key").GetString())
            .ShouldBe(["finder.notice.unknown_kind", "finder.notice.cursor_expired"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_listing_reads_in_the_language_asked_for()
    {
        RequireDatabase();
        SeedListing listing = PublishedListings().First(l => l.Translations.Any(t => t.Locale == "es"));

        (HttpResponseMessage response, JsonElement body) = await GetAsync($"api/v1/public/listings/{listing.Slug}", acceptLanguage: "es-MX,es;q=0.9,en;q=0.5");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLanguage.ShouldContain("es");
        body.GetProperty("title").GetString().ShouldBe(listing.Translations.First(t => t.Locale == "es").Title);
        body.GetProperty("contentLanguage").GetString().ShouldBe("es");

        // ?locale= wins over the header, for widgets and links.
        (HttpResponseMessage arabic, _) = await GetAsync($"api/v1/public/listings/{listing.Slug}?locale=ar", acceptLanguage: "es");
        arabic.Content.Headers.ContentLanguage.ShouldContain("ar");
    }

    [Fact]
    public async Task A_private_site_gives_its_name_but_never_its_address()
    {
        RequireDatabase();
        HashSet<Guid> privateSites = [.. api.Data.Sites.Where(s => !s.IsAddressPublic).Select(s => s.Id)];
        SeedListing listing = PublishedListings().First(l => l.SiteIds.Any(privateSites.Contains));
        SeedSite site = api.Data.Sites.First(s => privateSites.Contains(s.Id) && listing.SiteIds.Contains(s.Id));

        (HttpResponseMessage response, JsonElement body) = await GetAsync($"api/v1/public/listings/{listing.Slug}");
        string raw = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement place = body.GetProperty("places").EnumerateArray().First(p => p.GetProperty("name").GetString() == site.Name);
        place.GetProperty("isAddressPublic").GetBoolean().ShouldBeFalse();
        place.GetProperty("addressLine1").ValueKind.ShouldBe(JsonValueKind.Null);
        raw.ShouldNotContain(site.AddressLine1);

        SeedOrganisation org = api.Data.Organisations.Single(o => o.Id == site.OrgId);
        string profile = await (await GetAsync($"api/v1/public/organisations/{org.Slug}")).Response.Content.ReadAsStringAsync(Ct);
        profile.ShouldContain(site.Name);
        profile.ShouldNotContain(site.AddressLine1);
    }

    [Fact]
    public async Task Missing_things_are_not_found_problems()
    {
        RequireDatabase();
        foreach (string path in new[]
                 {
                     "api/v1/public/listings/no-such-listing", "api/v1/public/collections/no-such-collection",
                     "api/v1/public/neighbourhoods/no-such-hood", "api/v1/public/organisations/no-such-org",
                     "api/v1/public/taxonomy/skill",
                 })
        {
            (HttpResponseMessage response, JsonElement body) = await GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            body.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:not-found", path);
        }
    }

    [Fact]
    public async Task A_draft_is_not_published_through_the_api()
    {
        RequireDatabase();
        SeedListing draft = api.Data.Listings.First(l => l.Status == "draft");

        (await GetAsync($"api/v1/public/listings/{draft.Slug}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Curation_geography_taxonomy_and_the_tenant_are_readable()
    {
        RequireDatabase();

        JsonElement collections = (await GetAsync("api/v1/public/collections")).Body;
        collections.GetArrayLength().ShouldBeGreaterThan(0);
        string slug = collections[0].GetProperty("slug").GetString()!;
        JsonElement collection = (await GetAsync($"api/v1/public/collections/{slug}")).Body;
        collection.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);

        JsonElement hoods = (await GetAsync("api/v1/public/neighbourhoods")).Body;
        hoods.EnumerateArray().Select(h => h.GetProperty("code").GetString()).ShouldContain("hood-downtown");
        JsonElement downtown = (await GetAsync("api/v1/public/neighbourhoods/hood-downtown")).Body;
        downtown.GetProperty("boundary")[0].GetArrayLength().ShouldBeGreaterThanOrEqualTo(4);

        JsonElement categories = (await GetAsync("api/v1/public/taxonomy/category")).Body;
        categories.EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldContain("stem");

        JsonElement tenant = (await GetAsync("api/v1/public/tenant")).Body;
        tenant.GetProperty("locales").GetArrayLength().ShouldBe(6);
        tenant.GetProperty("locales").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "ar")
            .GetProperty("direction").GetString().ShouldBe("rtl");
    }

    [Fact]
    public async Task Another_host_is_another_tenant_or_nothing()
    {
        RequireDatabase();
        using HttpClient stranger = api.Client("unknown.localhost");

        (await stranger.GetAsync(new Uri("api/v1/public/listings", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private IEnumerable<SeedListing> PublishedListings() => api.Data.Listings
        .Where(l => l.Status == "published" && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
        .OrderBy(l => l.Slug, StringComparer.Ordinal);
}
