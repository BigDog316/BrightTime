using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Seed;

namespace BrightTime.Api.Tests;

/// <summary>
/// The signed-in person's own finder data through the API: saved listings that follow them
/// between devices, interests, where they search from, and the feed with its "why".
/// </summary>
/// <remarks>Admins 40 to 44, one per test (20 to 29 are ConsentApiTests'; 30 to 35 the email-plus tests').</remarks>
[Collection(ApiDatabase.Name)]
public sealed class MeFinderApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private IEnumerable<SeedListing> Published() => api.Data.Listings
        .Where(l => l.Status == "published" && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
        .OrderBy(l => l.Slug, StringComparer.Ordinal);

    private string[] InterestCodes => [.. api.Data.Terms.Where(t => t.Vocabulary == "interest").Select(t => t.Code).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_saved_listing_follows_the_person_and_carries_its_note()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(40));
        SeedListing listing = Published().First();

        (await me.PutAsJsonAsync($"api/v1/me/saved-items/{listing.Id}", new { note = "Ask about Saturdays" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await me.PutAsJsonAsync($"api/v1/me/saved-items/{listing.Id}", new { note = "Saturdays are full" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // From another sign-in, as from another device.
        using HttpClient other = await SignedInAsync(Admin(40));
        JsonElement saved = await other.GetFromJsonAsync<JsonElement>("api/v1/me/saved-items", Ct);
        saved.GetArrayLength().ShouldBe(1);
        saved[0].GetProperty("listing").GetProperty("slug").GetString().ShouldBe(listing.Slug);
        saved[0].GetProperty("note").GetString().ShouldBe("Saturdays are full");

        (await other.DeleteAsync(new Uri($"api/v1/me/saved-items/{listing.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await other.DeleteAsync(new Uri($"api/v1/me/saved-items/{listing.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/saved-items", Ct)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Only_a_published_listing_can_be_saved_and_nobody_sees_anyone_elses()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(41));
        SeedListing draft = api.Data.Listings.First(l => l.Status == "draft");

        HttpResponseMessage refused = await me.PutAsJsonAsync($"api/v1/me/saved-items/{draft.Id}", new { note = (string?)null }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:saved.listing_not_found");

        (await me.PutAsJsonAsync($"api/v1/me/saved-items/{Published().Skip(1).First().Id}", new { note = (string?)null }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using HttpClient stranger = await SignedInAsync(Admin(42));
        (await stranger.GetFromJsonAsync<JsonElement>("api/v1/me/saved-items", Ct)).GetArrayLength().ShouldBe(0);

        using HttpClient anonymous = api.Client();
        (await anonymous.GetAsync(new Uri("api/v1/me/saved-items", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Interests_are_codes_from_the_regions_vocabulary()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(43));
        string[] codes = InterestCodes;

        HttpResponseMessage set = await me.PutAsJsonAsync("api/v1/me/interests",
            new { interests = new[] { new { code = codes[0], weight = 5 }, new { code = codes[1], weight = 2 } } }, Ct);
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement mine = await me.GetFromJsonAsync<JsonElement>("api/v1/me/interests", Ct);
        mine.EnumerateArray().Select(i => (i.GetProperty("code").GetString(), i.GetProperty("weight").GetInt32()))
            .ShouldBe([(codes[0], 5), (codes[1], 2)]);
        mine[0].GetProperty("name").GetString().ShouldNotBeNullOrEmpty();

        HttpResponseMessage unknown = await me.PutAsJsonAsync("api/v1/me/interests",
            new { interests = new[] { new { code = "underwater-basket-weaving", weight = 3 } } }, Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:interests.unknown");
        problem.GetProperty("codes").EnumerateArray().Select(c => c.GetString()).ShouldBe(["underwater-basket-weaving"]);
    }

    [Fact]
    public async Task The_feed_matches_interests_near_the_anchor_and_says_why()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(44));

        // With nothing declared, the feed is what starts soonest.
        JsonElement plain = await me.GetFromJsonAsync<JsonElement>("api/v1/me/feed?limit=10", Ct);
        plain.GetProperty("items").GetArrayLength().ShouldBe(10);

        string interest = api.Data.Terms.Where(t => t.Vocabulary == "interest")
            .OrderByDescending(t => api.Data.Listings.Count(l => l.TermIds.Contains(t.Id))).First().Code;
        (await me.PutAsJsonAsync("api/v1/me/interests", new { interests = new[] { new { code = interest, weight = 4 } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.PutAsJsonAsync("api/v1/me/search-preferences", new { nearNeighbourhood = "hood-downtown", maxDistanceMetres = 8000 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/search-preferences", Ct)).GetProperty("nearNeighbourhood").GetString().ShouldBe("hood-downtown");

        JsonElement feed = await me.GetFromJsonAsync<JsonElement>("api/v1/me/feed?limit=50", Ct);
        JsonElement items = feed.GetProperty("items");
        items.GetArrayLength().ShouldBeGreaterThan(0);
        foreach (JsonElement item in items.EnumerateArray())
        {
            item.GetProperty("why").EnumerateArray().Select(w => w.GetProperty("key").GetString()).ShouldContain("finder.reason.matches_interests");
            if (item.GetProperty("distanceMetres").ValueKind == JsonValueKind.Number)
            {
                item.GetProperty("distanceMetres").GetDouble().ShouldBeLessThanOrEqualTo(8000);
            }
        }

        HttpResponseMessage bad = await me.PutAsJsonAsync("api/v1/me/search-preferences", new { nearNeighbourhood = "hood-nowhere" }, Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:anchor.neighbourhood");
    }
}
