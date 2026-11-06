using System.Net;
using System.Text.Json;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Pages;
using BrightTime.Ui.Shared.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrightTime.Ui.Shared.Tests;

/// <summary>
/// Playlists in the app (D67, D79), rendered from a stand-in API. The list shows the young person's
/// own and how far along they are, then the rest to try; a playlist offers Start only to an account
/// that can sign up for itself, and once started offers a step only when the server says it's open,
/// with what that step takes.
/// </summary>
public sealed class PlaylistScreenTests
{
    private const string River = "b1b10000-0000-0000-0000-000000000001";
    private const string Birds = "b1b10000-0000-0000-0000-000000000002";
    private const string Maps = "b1b10000-0000-0000-0000-000000000003";
    private const string Think = "57e90000-0000-0000-0000-000000000001";
    private const string Ranger = "57e90000-0000-0000-0000-000000000002";
    private const string Attend = "57e90000-0000-0000-0000-000000000003";
    private const string ReadUp = "57e90000-0000-0000-0000-000000000004";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Step(string id, int order, string kind, string title, bool required = true, bool review = false, string extra = "") => $$"""
        {"id":"{{id}}","order":{{order}},"kind":"{{kind}}","title":"{{title}}","instructions":{{(order == 1 ? "\"Write what you expect to find.\"" : "null")}},
         "listingId":{{(kind == "attend_listing" ? "\"1d1d0000-0000-0000-0000-000000000001\"" : "null")}},
         "externalUrl":{{(kind == "external_activity" ? "\"https://rivers.example.org/guide\"" : "null")}},"expectedMinutes":{{(order == 1 ? "30" : "null")}},
         "isRequired":{{(required ? "true" : "false")}},"requiresReview":{{(review ? "true" : "false")}}{{extra}}}
        """;

    private static readonly string[] RiverSteps =
    [
        Step(Think, 1, "reflection", "What lives in a river?"),
        Step(Ranger, 2, "mentor_checkin", "Check in with a ranger", review: true),
        Step(Attend, 3, "attend_listing", "Come to a session", required: false, extra: ""","listingTitle":"River Walk","listingSlug":"kettle-river-walk" """),
        Step(ReadUp, 4, "external_activity", "Read the river guide"),
    ];

    private static string Mine(string state2 = "needs_rework", bool finished = false) => $$"""
        [{"playlistId":"{{River}}","slug":"river-walk","title":"Watershed Explorer","organisation":"Kettle Parks","isOrdered":true,
          "startedAt":"2026-10-01T12:00:00Z","completedAt":null,"awardedAssertionId":null,"description":"Three steps\nalong the river.","completionBadge":"Watershed Explorer",
          "steps":[
            {"step":{{RiverSteps[0]}},"state":"completed","available":true,"reflection":"Mayfly nymphs under the stones.","reviewNote":null,"completedAt":"2026-10-02T12:00:00Z"},
            {"step":{{RiverSteps[1]}},"state":"{{state2}}","available":true,"reflection":"Told the ranger.","reviewNote":"Say what you found under the stones.","completedAt":null},
            {"step":{{RiverSteps[2]}},"state":"not_started","available":false,"reflection":null,"reviewNote":null,"completedAt":null},
            {"step":{{RiverSteps[3]}},"state":"not_started","available":false,"reflection":null,"reviewNote":null,"completedAt":null}]},
         {"playlistId":"{{Birds}}","slug":"bird-count","title":"Bird Count","organisation":"Kettle Audubon","isOrdered":false,
          "startedAt":"2026-09-01T12:00:00Z","completedAt":{{(finished ? "\"2026-09-20T12:00:00Z\"" : "null")}},"awardedAssertionId":{{(finished ? "\"a5a50000-0000-0000-0000-000000000001\"" : "null")}},
          "description":null,"completionBadge":"Bird Counter",
          "steps":[{"step":{{Step("57e90000-0000-0000-0000-000000000009", 1, "reflection", "Count the birds")}},"state":"completed","available":true,
                    "reflection":"Twelve sparrows and a heron.","reviewNote":null,"completedAt":"2026-09-20T12:00:00Z"}]}]
        """;

    private static string Public(string id, string slug, string title, string steps) => $$"""
        {"id":"{{id}}","slug":"{{slug}}","title":"{{title}}","description":"Make a map of the creek.","organisation":"Kettle Library","isOrdered":false,
         "ageMin":13,"ageMax":19,"estimatedHours":6.5,"completionBadge":"Mapper","completionBadgeClassId":null,"requiresBadge":"Bird Counter","steps":[{{steps}}]}
        """;

    private static readonly string MapsPlaylist = Public(Maps, "map-makers", "Map Makers", string.Join(",", RiverSteps[2], RiverSteps[3]));

    private static readonly string Published = "[" + Public(River, "river-walk", "Watershed Explorer", RiverSteps[0]) + "," + MapsPlaylist + "]";

    private static FakeApi SignedIn(string kind = "youth", string? capabilities = null) => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", capabilities ?? FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "me/playlists", Mine(finished: true))
        .Json(HttpMethod.Get, "public/playlists", Published)
        .Json(HttpMethod.Get, "public/playlists/map-makers", MapsPlaylist);

    [Fact]
    public async Task The_list_shows_their_playlists_and_how_far_along_then_the_rest_to_try()
    {
        using FakeApi api = SignedIn();
        string shown = Shown(await RenderAsync<Playlists>(api));

        shown.ShouldContain("Your playlists");
        shown.ShouldContain("From Kettle Parks");
        shown.ShouldContain("1 of 4 steps done");
        shown.ShouldContain("Finished on ");
        shown.ShouldContain("Playlists to try");
        shown.ShouldContain("href=\"playlists/map-makers\"");
        shown.Split("href=\"playlists/river-walk\"").Length.ShouldBe(2, "one started is listed once, as theirs");
        shown.ShouldContain("About 6.5 hours");
        shown.ShouldContain("For ages 13 to 19");
        shown.ShouldContain("Finish it to earn the Mapper badge");
        shown.ShouldContain("Opens to those who hold the Bird Counter badge");
        shown.ShouldNotContain("app.playlists");
    }

    [Fact]
    public async Task With_none_started_and_none_published_the_list_says_so()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/playlists", "[]").Json(HttpMethod.Get, "public/playlists", "[]");
        string shown = Shown(await RenderAsync<Playlists>(api));

        shown.ShouldContain("You haven't started a playlist yet.");
        shown.ShouldContain("There are no playlists yet.");
    }

    [Fact]
    public async Task A_started_playlist_offers_only_the_steps_the_server_says_are_open()
    {
        using FakeApi api = SignedIn();
        string shown = Shown(await RenderAsync<Playlist>(api, Slug("river-walk")));

        shown.ShouldContain("<h1 dir=\"auto\">Watershed Explorer</h1>");
        shown.ShouldContain("1 of 4 steps done");
        shown.ShouldContain("Do the steps in order.");
        shown.ShouldContain("Step 1: What lives in a river?");
        shown.ShouldContain("Write about it · About 30 minutes");
        shown.ShouldContain("Write what you expect to find.");
        shown.ShouldContain("Done on ");
        shown.ShouldContain("What you wrote:");
        shown.ShouldContain("Mayfly nymphs under the stones.");

        shown.ShouldContain("Check in with staff · Staff check this step");
        shown.ShouldContain("Sent back to you: Say what you found under the stones.");
        shown.ShouldContain($"Told the ranger.</textarea>", Case.Sensitive, "what they sent before is there to change");
        shown.Split(">Send to staff</button>").Length.ShouldBe(2, "only the step sent back is open");
        shown.ShouldNotContain(">Mark it done</button>", Case.Sensitive, "the steps after it wait for it");

        shown.ShouldContain("Go to an activity · Optional");
        shown.ShouldContain("href=\"listings/kettle-river-walk\">Go to River Walk</a>");
        shown.Split("Finish the steps before this one first.").Length.ShouldBe(3);
        shown.ShouldContain(">Open the activity</button>");
        shown.ShouldContain("<p class=\"hint\" dir=\"ltr\">rivers.example.org</p>");
        shown.ShouldNotContain("app.playlists");
    }

    [Fact]
    public async Task A_step_to_attend_has_nothing_to_write_and_others_take_a_note()
    {
        string open = Mine("completed").Replace("\"available\":false", "\"available\":true", StringComparison.Ordinal);
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/playlists", open);
        string shown = Shown(await RenderAsync<Playlist>(api, Slug("river-walk")));

        shown.ShouldContain("2 of 4 steps done");
        shown.Split(">Mark it done</button>").Length.ShouldBe(3, "the step to attend and the activity elsewhere");
        shown.ShouldNotContain($"id=\"say-{Attend.Replace("-", "", StringComparison.Ordinal)}\"", Case.Sensitive, "attending is shown by attendance, not written");
        shown.ShouldContain("Add a note, if you like");
        shown.ShouldContain("Read the river guide");
    }

    [Fact]
    public async Task Before_starting_it_says_what_it_is_and_offers_start()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/playlists", "[]");
        string shown = Shown(await RenderAsync<Playlist>(api, Slug("map-makers")));

        shown.ShouldContain("Map Makers");
        shown.ShouldContain("From Kettle Library");
        shown.ShouldContain("Make a map of the creek.");
        shown.ShouldContain("Opens to those who hold the Bird Counter badge");
        shown.ShouldContain("Do the steps in any order.");
        shown.ShouldContain(">Start this playlist</button>");
        shown.ShouldContain("Step 3: Come to a session");
        shown.ShouldNotContain("<form", Case.Sensitive, "nothing to do until it's started");
    }

    [Fact]
    public async Task An_account_that_cannot_sign_up_for_itself_is_told_so_instead_of_offered_start()
    {
        string under13 = FakeApi.Capabilities.Replace("\"canSelfEnroll\":true", "\"canSelfEnroll\":false", StringComparison.Ordinal);
        using FakeApi api = SignedIn("child", under13).Json(HttpMethod.Get, "me/playlists", "[]");
        string shown = Shown(await RenderAsync<Playlist>(api, Slug("map-makers")));

        shown.ShouldContain("Your account can't start playlists yet.");
        shown.ShouldNotContain(">Start this playlist</button>");
    }

    [Fact]
    public async Task A_finished_playlist_says_when_and_points_to_the_badge()
    {
        using FakeApi api = SignedIn();
        string shown = Shown(await RenderAsync<Playlist>(api, Slug("bird-count")));

        shown.ShouldContain("You finished this playlist on ");
        shown.ShouldContain("You earned the Bird Counter badge. It's on your portfolio.");
        shown.ShouldContain("href=\"portfolio\"");
        shown.ShouldNotContain("<form");
    }

    [Fact]
    public async Task An_archived_playlist_stays_with_those_on_it_and_an_unknown_one_says_so()
    {
        using FakeApi api = SignedIn();
        Shown(await RenderAsync<Playlist>(api, Slug("river-walk"))).ShouldNotContain("role=\"alert\"", Case.Sensitive, "gone from the public list, it's still theirs");
        Shown(await RenderAsync<Playlist>(api, Slug("no-such-thing"))).ShouldContain("There's no playlist like that.");
    }

    [Fact]
    public void An_activity_elsewhere_opens_only_over_https()
    {
        PlaylistStep step = JsonSerializer.Deserialize<PlaylistStep>(RiverSteps[3], AppJson)!;
        Pages.Playlist.Link(step).ShouldBe(new Uri("https://rivers.example.org/guide"));
        Pages.Playlist.Link(step with { ExternalUrl = "http://rivers.example.org/" }).ShouldBeNull();
        Pages.Playlist.Link(step with { ExternalUrl = "javascript:alert(1)" }).ShouldBeNull();
    }

    [Fact]
    public async Task The_account_menu_leads_a_young_person_to_playlists()
    {
        using FakeApi youth = SignedIn();
        (await RenderAsync<Account>(youth)).ShouldContain("href=\"playlists\"");
        using FakeApi adult = SignedIn("adult");
        (await RenderAsync<Account>(adult)).ShouldNotContain("href=\"playlists\"");
    }

    [Fact]
    public async Task Playlists_go_to_the_routes_the_server_has()
    {
        List<(string Method, string Path, string? Body)> sent = [];
        string one = JsonDocument.Parse(Mine()).RootElement[0].GetRawText();
        HttpResponseMessage Record(HttpRequestMessage r)
        {
            sent.Add((r.Method.Method, r.RequestUri!.AbsolutePath, r.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return FakeApi.Respond(HttpStatusCode.OK, one);
        }

        using FakeApi api = SignedIn()
            .On(HttpMethod.Post, $"playlists/{River}/enroll", Record)
            .On(HttpMethod.Put, $"me/playlists/{River}/steps/{Ranger}", Record);
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("kofi@example.net", "pw", Ct)).ShouldBeNull();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        (await client.PlaylistsAsync(Ct)).Value!.Count.ShouldBe(2);
        (await client.PlaylistAsync("map-makers", Ct)).Value!.RequiresBadge.ShouldBe("Bird Counter");
        IReadOnlyList<MyPlaylist> mine = (await client.MyPlaylistsAsync(Ct)).Value!;
        (mine[0].Steps[2].Step.ListingSlug, mine[0].Steps[1].IsOpen, mine[0].Steps[0].IsOpen).ShouldBe(("kettle-river-walk", true, false));
        (await client.StartPlaylistAsync(Guid.Parse(River), Ct)).Value!.Slug.ShouldBe("river-walk");
        (await client.DoStepAsync(Guid.Parse(River), Guid.Parse(Ranger), "Found a crayfish.", Ct)).Value.ShouldNotBeNull();

        sent.Select(s => $"{s.Method} {s.Path}").ShouldBe([$"POST /api/v1/playlists/{River}/enroll", $"PUT /api/v1/me/playlists/{River}/steps/{Ranger}"]);
        using JsonDocument body = JsonDocument.Parse(sent[1].Body!);
        body.RootElement.GetProperty("reflection").GetString().ShouldBe("Found a crayfish.");
    }

    private static readonly JsonSerializerOptions AppJson = new(JsonSerializerDefaults.Web);

    private static Dictionary<string, object?> Slug(string slug) => new() { ["Slug"] = slug };

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static async Task<string> RenderAsync<TComponent>(FakeApi api, IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        await using ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("kofi@example.net", "pw", Ct)).ShouldBeNull();
        await using HtmlRenderer renderer = new(app, app.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(new Dictionary<string, object?>(parameters)));
            await root.QuiescenceTask;
            return root.ToHtmlString();
        });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://app.test/", "https://app.test/");

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
