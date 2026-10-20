using System.Net;
using BrightTime.Localisation;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Pages;
using BrightTime.Ui.Shared.Platform;
using BrightTime.Ui.Shared.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrightTime.Ui.Shared.Tests;

/// <summary>The screens' logic, and two screens rendered to HTML from a stand-in API.</summary>
public sealed class UiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_search_writes_only_the_filters_that_are_set()
    {
        new SearchFilters().ToQuery().ShouldBeEmpty();
        new SearchFilters
        {
            Text = " art & music ",
            Kinds = ["program", "event"],
            Categories = ["arts"],
            Age = 14,
            FreeOnly = true,
            NearNeighbourhood = "hood-downtown",
            Sort = SearchFilters.Soonest,
        }.ToQuery("abc", 10).ShouldBe("q=art%20%26%20music&kind=program&kind=event&category=arts&age=14&free=true&nearNeighbourhood=hood-downtown&sort=soonest&cursor=abc&limit=10");
        new SearchFilters { Kinds = ["job"], DropIn = true, Age = 15 }.Count.ShouldBe(3);
    }

    [Fact]
    public void Only_this_sites_app_paths_are_opened_from_a_link()
    {
        AppLinks links = new();
        links.Open(new Uri("https://kettle.example.org/account/reset?token=x")).ShouldBeFalse();
        links.Take().ShouldBeNull();

        links.Open(new Uri("https://kettle.example.org/app/guardian-links/7a1c0000-0000-0000-0000-000000000001/verify#token=abc")).ShouldBeTrue();
        links.Take().ShouldBe("/app/guardian-links/7a1c0000-0000-0000-0000-000000000001/verify#token=abc");
        links.Take().ShouldBeNull("a link is opened once");
    }

    [Fact]
    public void The_nearest_neighbourhood_is_chosen_on_the_device()
    {
        NeighbourhoodSummary[] hoods =
        [
            new("north", "North", 3, new Coordinate(-73.80, 42.70)),
            new("south", "South", 5, new Coordinate(-73.80, 42.60)),
        ];

        Nearest.Neighbourhood(hoods, new GeoPoint(42.62, -73.79)).Code.ShouldBe("south");
        Nearest.Metres(new GeoPoint(42.60, -73.80), new GeoPoint(42.70, -73.80)).ShouldBe(11_119, tolerance: 20);
    }

    [Fact]
    public async Task Problems_are_worded_in_the_apps_language_never_in_the_servers_english()
    {
        using FakeApi api = new FakeApi().Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant);
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        app.GetRequiredService<AppState>().ChooseLocale("es");
        Text t = app.GetRequiredService<Text>();

        Words.Problem(t, new ApiProblem("urn:brighttime:problem:consent.token_invalid", "English title", 403)).ShouldBe(t["error.consent.token_invalid"]);
        Words.Problem(t, ApiProblem.Offline).ShouldBe(t["app.error.offline"]);
        Words.Problem(t, new ApiProblem("urn:brighttime:problem:no.such_code", "English title", 409)).ShouldBe(t["site.error.body"]);
        Words.Problem(t, new ApiProblem("urn:brighttime:problem:no.such_code", "English title", 409)).ShouldNotContain("English");
    }

    [Fact]
    public void The_age_question_reads_the_younger_way_as_the_server_does()
    {
        DateOnly today = new(2026, 10, 20);
        Words.IsUnder13(2013, 10, today).ShouldBeTrue("born late in October 2013, they may not be 13 until the 31st");
        Words.IsUnder13(2013, 9, today).ShouldBeFalse();
        Words.BirthMonth(2013, 9).ShouldBe("2013-09");
    }

    [Fact]
    public async Task Home_shows_what_starts_soonest_and_the_way_in_to_someone_signed_out()
    {
        using FakeApi api = new FakeApi()
            .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
            .Json(HttpMethod.Get, "public/listings", FakeApi.Results);
        string html = await RenderAsync<Home>(api);

        html.ShouldContain("Robotics Club");
        html.ShouldContain("Create an account");
        html.ShouldContain("starts in 2 days", Case.Insensitive);
        html.ShouldNotContain("no.such.key");
    }

    [Fact]
    public async Task Account_lists_a_young_persons_capabilities_as_the_server_gave_them()
    {
        using FakeApi api = new FakeApi()
            .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
            .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
            .Json(HttpMethod.Get, "me", FakeApi.Me("youth"))
            .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities);
        string html = await RenderAsync<Account>(api, signIn: true);

        html.ShouldContain("You can sign up for programs yourself.");
        html.ShouldContain("You can&#x27;t upload photos yet.");
        html.ShouldContain("signed out when you close the app");
    }

    private static async Task<string> RenderAsync<TComponent>(FakeApi api, bool signIn = false)
        where TComponent : IComponent
    {
        await using ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        if (signIn)
        {
            (await app.GetRequiredService<AppSession>().SignInAsync("teen@example.net", "pw", Ct)).ShouldBeNull();
        }

        await using HtmlRenderer renderer = new(app, app.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>();
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
