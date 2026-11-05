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
/// A young person's portfolio in the app (D77), rendered from a stand-in API: who sees it and its
/// link, the hours and activities with hidden ones marked, the badges with their standing, and the
/// settings screen, which explains what the server refuses rather than deciding it.
/// </summary>
public sealed class PortfolioScreenTests
{
    private const string Rowing = "7a7a0000-0000-0000-0000-0000000000c1";
    private const string Chess = "7a7a0000-0000-0000-0000-0000000000c2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Portfolio(string visibility = "link_only", string? slug = "kofi-rows", bool canShow = true) => $$"""
        {"displayName":"Kofi R","headline":"Rower and chess player","bio":null,"visibility":"{{visibility}}",
         "publicSlug":{{(slug is null ? "null" : $"\"{slug}\"")}},"totalHours":12.5,"canShow":{{(canShow ? "true" : "false")}},
         "participation":[
           {"id":"{{Rowing}}","title":"Junior Rowing","organisation":"Kettle Rowing Club","startedOn":"2026-09-01","endedOn":null,
            "sessionsAttended":5,"hours":12.5,"source":"attendance","isHidden":false},
           {"id":"{{Chess}}","title":"Chess Club","organisation":null,"startedOn":"2026-06-01","endedOn":"2026-07-30",
            "sessionsAttended":3,"hours":4,"source":"attendance","isHidden":true}]}
        """;

    private const string Badges = """
        [{"id":"b0b00000-0000-0000-0000-000000000001","name":"River Surveyor","issuer":"Kettle Rowing Club","issuedOn":"2026-10-01T15:00:00Z",
          "expiresOn":"2099-10-01T15:00:00Z","revoked":false,"verify":"https://kettle.test/api/v1/public/badges/abc","credential":"eyJ","page":"https://kettle.test/badges/abc"},
         {"id":"b0b00000-0000-0000-0000-000000000002","name":"First Aid","issuer":"Kettle Library","issuedOn":"2026-08-01T15:00:00Z",
          "expiresOn":null,"revoked":true,"verify":"https://kettle.test/api/v1/public/badges/def","credential":"eyJ","page":"https://kettle.test/badges/def"}]
        """;

    private static FakeApi SignedIn(string kind = "youth") => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "me/badges", Badges);

    [Fact]
    public async Task The_portfolio_shows_who_sees_it_the_hours_and_activities_and_the_badges()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/portfolio", Portfolio());
        string shown = Shown(await RenderAsync<Portfolio>(api));

        shown.ShouldContain("Anyone you give the link to. Search engines don't show it.");
        shown.ShouldContain("https://kettle.test/portfolios/kofi-rows", Case.Sensitive, "the link it is shown at, to share");
        shown.ShouldContain(">Share</button>");
        shown.ShouldContain("12.5 hours in all.");
        shown.ShouldContain("Junior Rowing");
        shown.ShouldContain("Kettle Rowing Club");
        shown.ShouldContain("Sessions: 5 · Hours: 12.5");
        shown.ShouldContain("<li class=\"card hidden-record\">", Case.Sensitive, "the hidden record stays in view, set apart");
        shown.ShouldContain("Hidden. It's left out of your total and anything you show.");
        shown.ShouldContain(">Show again</button>");
        shown.ShouldContain(">Hide</button>");
        shown.ShouldContain("River Surveyor");
        shown.ShouldContain("From Kettle Rowing Club");
        shown.ShouldContain("Expires ");
        shown.ShouldContain("Revoked: the organisation took this badge back.");
        shown.ShouldNotContain("eyJ", Case.Sensitive, "the credential is for other software, not the screen");
        shown.ShouldNotContain("app.portfolio");
        shown.ShouldNotContain("portfolio.page");
    }

    [Fact]
    public async Task A_private_portfolio_has_no_link_and_someone_under_13_is_told_why()
    {
        using FakeApi api = SignedIn("child").Json(HttpMethod.Get, "me/portfolio", Portfolio("private", null, canShow: false));
        string shown = Shown(await RenderAsync<Portfolio>(api));

        shown.ShouldContain("Only you.");
        shown.ShouldNotContain("kettle.test/portfolios");
        shown.ShouldContain("Showing your portfolio outside the platform isn't open to you yet.");
    }

    [Fact]
    public async Task Nothing_yet_says_where_the_records_and_badges_will_come_from()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/badges", "[]").Json(HttpMethod.Get, "me/portfolio", """
            {"displayName":null,"headline":null,"bio":null,"visibility":"private","publicSlug":null,"totalHours":0,"canShow":true,"participation":[]}
            """);
        string shown = Shown(await RenderAsync<Portfolio>(api));

        shown.ShouldContain("Nothing yet. Programs you attend show here, with their hours.");
        shown.ShouldContain("No badges yet.");
    }

    [Fact]
    public async Task An_adult_has_no_portfolio_and_no_link_to_one()
    {
        using FakeApi api = SignedIn("adult");
        Shown(await RenderAsync<Portfolio>(api)).ShouldContain("A portfolio belongs to a young person's own account.");
        (await RenderAsync<Account>(api)).ShouldNotContain("href=\"portfolio\"");

        using FakeApi youth = SignedIn();
        (await RenderAsync<Account>(youth)).ShouldContain("href=\"portfolio\"");
    }

    [Fact]
    public async Task The_settings_screen_fills_in_what_is_set_and_previews_the_link()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/portfolio", Portfolio());
        string shown = Shown(await RenderAsync<PortfolioSettings>(api));

        shown.ShouldContain("value=\"Kofi R\"");
        shown.ShouldContain("value=\"Rower and chess player\"");
        shown.ShouldMatch("value=\"link_only\" checked");
        shown.ShouldContain("value=\"kofi-rows\"");
        shown.ShouldContain("https://kettle.test/portfolios/kofi-rows");
        shown.ShouldNotContain("disabled", Case.Sensitive, "a young person who may show it can choose any option");
        shown.ShouldNotContain("app.portfolio");
    }

    [Fact]
    public async Task A_link_name_not_yet_chosen_previews_nothing()
    {
        // Found on the emulator: an empty name previewed the link with an encoded ellipsis in it.
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/portfolio", Portfolio("link_only", null));
        string html = await RenderAsync<PortfolioSettings>(api);

        html.ShouldContain("id=\"slug\"");
        html.ShouldNotContain("/portfolios/");
        html.ShouldNotContain("%E2%80%A6");
    }

    [Fact]
    public async Task Under_13_the_settings_offer_only_private_and_say_why()
    {
        using FakeApi api = SignedIn("child").Json(HttpMethod.Get, "me/portfolio", Portfolio("private", null, canShow: false));
        string shown = Shown(await RenderAsync<PortfolioSettings>(api));

        shown.ShouldMatch("value=\"private\" checked");
        shown.ShouldMatch("value=\"link_only\" disabled");
        shown.ShouldMatch("value=\"public\" disabled");
        shown.ShouldContain("Showing your portfolio outside the platform isn't open to you yet.");
        shown.ShouldNotContain("id=\"slug\"", Case.Sensitive, "no link name for a private portfolio");
    }

    [Fact]
    public async Task Hiding_saving_and_the_badges_go_to_the_routes_the_server_has()
    {
        List<(string Path, string? Body)> sent = [];
        HttpResponseMessage Record(HttpRequestMessage r)
        {
            sent.Add((r.RequestUri!.AbsolutePath, r.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return FakeApi.Respond(HttpStatusCode.OK, Portfolio());
        }

        using FakeApi api = SignedIn()
            .On(HttpMethod.Post, $"me/portfolio/participation/{Rowing}/hide", Record)
            .On(HttpMethod.Post, $"me/portfolio/participation/{Chess}/show", Record)
            .On(HttpMethod.Put, "me/portfolio", Record)
            .On(HttpMethod.Put, "me/portfolio/", Record);
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("kofi@example.net", "pw", Ct)).ShouldBeNull();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        (await client.HideParticipationAsync(Guid.Parse(Rowing), hidden: true, Ct)).Value.ShouldNotBeNull();
        (await client.HideParticipationAsync(Guid.Parse(Chess), hidden: false, Ct)).Value.ShouldNotBeNull();
        (await client.SavePortfolioAsync(new PortfolioDraft("Kofi R", null, null, "public", "kofi-rows"), Ct)).Value.ShouldNotBeNull();
        IReadOnlyList<HeldBadge> badges = (await client.HeldBadgesAsync(Ct)).Value!;

        sent.Select(s => s.Path).ShouldBe([$"/api/v1/me/portfolio/participation/{Rowing}/hide", $"/api/v1/me/portfolio/participation/{Chess}/show", "/api/v1/me/portfolio"]);
        using JsonDocument saved = JsonDocument.Parse(sent[2].Body!);
        saved.RootElement.GetProperty("visibility").GetString().ShouldBe("public");
        saved.RootElement.GetProperty("publicSlug").GetString().ShouldBe("kofi-rows");
        badges[0].Page.ShouldBe(new Uri("https://kettle.test/badges/abc"));
    }

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
