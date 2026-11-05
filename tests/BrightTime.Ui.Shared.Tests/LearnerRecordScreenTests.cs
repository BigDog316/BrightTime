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
/// A young person's goals, skills and projects in the app (D78), rendered from a stand-in API. Each
/// screen offers only the next step the server allows from where a record stands, and says why
/// when it can't: no activity chosen, or under 13 and so not able to show anything.
/// </summary>
public sealed class LearnerRecordScreenTests
{
    private const string Walk = "9a9a0000-0000-0000-0000-000000000001";
    private const string Done = "9a9a0000-0000-0000-0000-000000000002";
    private const string Teamwork = "5c5c0000-0000-0000-0000-000000000001";
    private const string Fieldwork = "5c5c0000-0000-0000-0000-000000000002";
    private const string Maps = "5c5c0000-0000-0000-0000-000000000003";
    private const string Rivers = "5c5c0000-0000-0000-0000-000000000004";
    private const string Rowing = "1d1d0000-0000-0000-0000-000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Terms = $$"""
        [{"id":"{{Teamwork}}","vocabulary":"skill","name":"Teamwork","parentId":null},
         {"id":"{{Fieldwork}}","vocabulary":"skill","name":"Field research","parentId":null},
         {"id":"{{Maps}}","vocabulary":"skill","name":"Map reading","parentId":null},
         {"id":"{{Rivers}}","vocabulary":"interest","name":"Rivers","parentId":null}]
        """;

    private const string GoalList = $$"""
        [{"id":"{{Walk}}","title":"Lead a river walk","detail":"For the younger group","targetTermId":"{{Rivers}}","target":"Rivers",
          "targetDate":"2027-06-01","status":"active","createdAt":"2026-10-01T12:00:00Z","achievedAt":null},
         {"id":"{{Done}}","title":"Swim a length","detail":null,"targetTermId":null,"target":null,
          "targetDate":null,"status":"achieved","createdAt":"2026-09-01T12:00:00Z","achievedAt":"2026-10-20T12:00:00Z"}]
        """;

    private const string SkillList = """
        [{"id":"aa000000-0000-0000-0000-000000000001","termId":"5c5c0000-0000-0000-0000-000000000002","name":"Field research","level":3,"endorsed":true,
          "endorsedBy":"Kettle Rowing Club","assertedAt":"2026-10-02T12:00:00Z"},
         {"id":"aa000000-0000-0000-0000-000000000002","termId":"5c5c0000-0000-0000-0000-000000000001","name":"Teamwork","level":2,"endorsed":false,
          "endorsedBy":null,"assertedAt":"2026-10-01T12:00:00Z"}]
        """;

    private static string Project(string id, string title, string status, string visibility = "private", string? listing = Rowing, string? note = null) => $$"""
        {"id":"{{id}}","title":"{{title}}","description":"Line one\nLine two","listingId":{{(listing is null ? "null" : $"\"{listing}\"")}},
         "activity":{{(listing is null ? "null" : "\"Junior Rowing\"")}},"organisation":{{(listing is null ? "null" : "\"Kettle Rowing Club\"")}},
         "status":"{{status}}","visibility":"{{visibility}}","reviewNote":{{(note is null ? "null" : $"\"{note}\"")}},"approvedAt":null,"updatedAt":"2026-10-03T12:00:00Z"}
        """;

    private static readonly string ProjectList = "[" + string.Join(",",
        Project("e0000000-0000-0000-0000-000000000001", "Mussel map", "draft", note: "Add a key to the map."),
        Project("e0000000-0000-0000-0000-000000000002", "River poem", "draft", listing: null),
        Project("e0000000-0000-0000-0000-000000000003", "Boat sketch", "approved"),
        Project("e0000000-0000-0000-0000-000000000004", "Race report", "published", "link_only")) + "]";

    private static string Portfolio(string visibility = "private", bool canShow = true) => $$"""
        {"displayName":"Kofi R","headline":null,"bio":null,"visibility":"{{visibility}}","publicSlug":null,"totalHours":12.5,"canShow":{{(canShow ? "true" : "false")}},
         "participation":[
           {"id":"7a7a0000-0000-0000-0000-0000000000c1","title":"Junior Rowing","organisation":"Kettle Rowing Club","startedOn":"2026-09-01","endedOn":null,
            "sessionsAttended":5,"hours":12.5,"source":"attendance","isHidden":false,"listingId":"{{Rowing}}"},
           {"id":"7a7a0000-0000-0000-0000-0000000000c2","title":"Junior Rowing","organisation":"Kettle Rowing Club","startedOn":"2026-03-01","endedOn":"2026-05-01",
            "sessionsAttended":4,"hours":6,"source":"attendance","isHidden":false,"listingId":"{{Rowing}}"},
           {"id":"7a7a0000-0000-0000-0000-0000000000c3","title":"Summer reading","organisation":null,"startedOn":"2025-07-01","endedOn":"2025-08-01",
            "sessionsAttended":0,"hours":0,"source":"import","isHidden":false,"listingId":null}]}
        """;

    private static FakeApi SignedIn(string kind = "youth") => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "me/learning/terms", Terms)
        .Json(HttpMethod.Get, "me/goals", GoalList)
        .Json(HttpMethod.Get, "me/skills", SkillList)
        .Json(HttpMethod.Get, "me/projects", ProjectList);

    [Fact]
    public async Task Goals_say_where_each_stands_and_offer_only_the_moves_from_there()
    {
        using FakeApi api = SignedIn();
        string shown = Shown(await RenderAsync<Goals>(api));

        shown.ShouldContain("Your goals are just for you. They never show on your portfolio page.");
        shown.ShouldContain("Lead a river walk");
        shown.ShouldContain("Working on it");
        shown.ShouldContain("About: Rivers");
        shown.ShouldContain("Aiming for ");
        shown.ShouldContain(">I did it</button>");
        shown.ShouldContain(">Put it on hold</button>");
        shown.ShouldContain(">Set it aside</button>");
        shown.ShouldContain("Done on ");
        shown.ShouldContain(">Work on it again</button>", Case.Sensitive, "a goal that's done can be taken up again");
        shown.ShouldContain($"href=\"portfolio/goals/{Walk}\"");
        shown.ShouldNotContain("app.goals");
        Goals.Moves("achieved").ShouldBe(["active"]);
        Goals.Moves("paused").ShouldBe(["active", "abandoned"]);
    }

    [Fact]
    public async Task A_goal_opens_with_what_it_says_and_offers_skills_and_interests_apart()
    {
        using FakeApi api = SignedIn();
        string html = await RenderAsync<GoalForm>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Walk) });
        string shown = Shown(html);

        shown.ShouldContain("Change your goal");
        shown.ShouldContain("value=\"Lead a river walk\"");
        shown.ShouldContain("For the younger group</textarea>");
        shown.ShouldContain("value=\"2027-06-01\"");
        shown.ShouldContain("<optgroup label=\"Skills\">");
        shown.ShouldContain("<optgroup label=\"Interests\">");
        shown.ShouldMatch($"<option value=\"{Rivers}\" selected[^>]*>Rivers</option>");
        shown.ShouldNotContain("app.goals");
    }

    [Fact]
    public async Task A_goal_that_is_gone_says_so()
    {
        using FakeApi api = SignedIn();
        Shown(await RenderAsync<GoalForm>(api, new Dictionary<string, object?> { ["Id"] = Guid.NewGuid() })).ShouldContain("You have no goal like that.");
    }

    [Fact]
    public async Task Skills_show_their_level_and_who_endorsed_them_and_offer_only_skills_not_yet_listed()
    {
        using FakeApi api = SignedIn();
        string shown = Shown(await RenderAsync<Skills>(api));

        shown.ShouldContain("Field research");
        shown.ShouldContain("Proficient");
        shown.ShouldContain("Endorsed by Kettle Rowing Club");
        shown.Split("Developing").Length.ShouldBe(3, "their own skill's level is said once, in its drop-down (plus the add form's option)");
        shown.Split("id=\"level-").Length.ShouldBe(2, "only the young person's own skill has a level they set");
        shown.ShouldContain(">Remove</button>");

        string offered = shown[shown.IndexOf("id=\"new-skill\"", StringComparison.Ordinal)..];
        offered.ShouldContain($"<option value=\"{Maps}\">Map reading</option>");
        offered.ShouldContain($"<option value=\"{Fieldwork}\">Field research</option>", Case.Sensitive, "endorsed by an organisation, it can still be listed as their own");
        offered.ShouldNotContain($"value=\"{Teamwork}\"", Case.Sensitive, "already listed");
        offered.ShouldNotContain("Rivers", Case.Sensitive, "an interest isn't a skill");
        shown.ShouldNotContain("app.skills");
    }

    [Fact]
    public async Task With_no_skills_on_the_county_list_there_is_nothing_to_add()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/skills", "[]").Json(HttpMethod.Get, "me/learning/terms", "[]");
        string shown = Shown(await RenderAsync<Skills>(api));

        shown.ShouldContain("No skills yet.");
        shown.ShouldContain("This county hasn't made its list of skills yet.");
        shown.ShouldNotContain("id=\"new-skill\"");
    }

    [Fact]
    public async Task Projects_say_where_each_stands_and_offer_the_next_step_from_there()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/portfolio", Portfolio("private"));
        string shown = Shown(await RenderAsync<Projects>(api));

        shown.ShouldContain("From Junior Rowing · Kettle Rowing Club");
        shown.ShouldContain("Draft. Only you can see it.");
        shown.ShouldContain("Sent back to you: Add a key to the map.");
        shown.ShouldContain("<p class=\"project-text\" dir=\"auto\">Line one\nLine two</p>");
        shown.Split(">Send for approval</button>").Length.ShouldBe(2, "only the draft with an activity can be sent");
        shown.ShouldContain("Choose the activity it came from. Then you can send it for approval.");
        shown.ShouldContain("Approved. Only you can see it until you show it.");
        shown.ShouldContain("Shown to anyone with your portfolio link.");
        shown.ShouldContain(">Show with your link</button>");
        shown.ShouldContain(">Show to everyone</button>");
        shown.ShouldContain(">Stop showing it</button>");
        shown.ShouldContain("Your portfolio is just for you right now, so nobody else sees your shown projects yet.");
        shown.ShouldNotContain("app.projects");
    }

    [Fact]
    public async Task Under_13_an_approved_project_cannot_be_shown_and_the_screen_says_why()
    {
        using FakeApi api = SignedIn("child").Json(HttpMethod.Get, "me/portfolio", Portfolio(canShow: false));
        string shown = Shown(await RenderAsync<Projects>(api));

        shown.ShouldContain("Showing your work outside the platform isn't open to you yet.");
        shown.ShouldNotContain(">Show to everyone</button>");
        shown.ShouldNotContain(">Show with your link</button>");
    }

    [Fact]
    public async Task A_project_is_about_an_activity_taken_part_in_and_changing_an_approved_one_warns_first()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/portfolio", Portfolio());
        string shown = Shown(await RenderAsync<ProjectForm>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse("e0000000-0000-0000-0000-000000000003") }));

        shown.ShouldContain("value=\"Boat sketch\"");
        shown.ShouldContain("Changing it makes it a draft again.");
        shown.Split($"<option value=\"{Rowing}\"").Length.ShouldBe(2, "an activity taken part in twice is offered once");
        shown.ShouldMatch($"<option value=\"{Rowing}\" selected[^>]*>Junior Rowing · Kettle Rowing Club</option>");
        shown.ShouldNotContain("Summer reading", Case.Sensitive, "a record from elsewhere has no activity here to approve it");

        string fresh = Shown(await RenderAsync<ProjectForm>(api));
        fresh.ShouldContain("Add a project");
        fresh.ShouldNotContain("Changing it makes it a draft again.");
    }

    [Fact]
    public async Task The_portfolio_leads_to_skills_projects_and_goals()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/portfolio", Portfolio()).Json(HttpMethod.Get, "me/badges", "[]");
        string html = await RenderAsync<Portfolio>(api);

        html.ShouldContain("href=\"portfolio/skills\"");
        html.ShouldContain("href=\"portfolio/projects\"");
        html.ShouldContain("href=\"portfolio/goals\"");
    }

    [Fact]
    public async Task Goals_skills_and_projects_go_to_the_routes_the_server_has()
    {
        List<(string Method, string Path, string? Body)> sent = [];
        HttpResponseMessage Record(HttpRequestMessage r, string body)
        {
            sent.Add((r.Method.Method, r.RequestUri!.AbsolutePath, r.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return FakeApi.Respond(body.Length == 0 ? HttpStatusCode.NoContent : HttpStatusCode.OK, body.Length == 0 ? null : body);
        }

        string goal = JsonDocument.Parse(GoalList).RootElement[0].GetRawText();
        string skill = JsonDocument.Parse(SkillList).RootElement[1].GetRawText();
        string project = Project("e0000000-0000-0000-0000-000000000001", "Mussel map", "submitted");
        Guid projectId = Guid.Parse("e0000000-0000-0000-0000-000000000001");
        Guid skillId = Guid.Parse("aa000000-0000-0000-0000-000000000002");

        using FakeApi api = SignedIn()
            .On(HttpMethod.Post, "me/goals", r => Record(r, goal))
            .On(HttpMethod.Put, $"me/goals/{Walk}", r => Record(r, goal))
            .On(HttpMethod.Post, "me/skills", r => Record(r, skill))
            .On(HttpMethod.Delete, $"me/skills/{skillId}", r => Record(r, ""))
            .On(HttpMethod.Post, "me/projects", r => Record(r, project))
            .On(HttpMethod.Put, $"me/projects/{projectId}", r => Record(r, project))
            .On(HttpMethod.Post, $"me/projects/{projectId}/submit", r => Record(r, project))
            .On(HttpMethod.Put, $"me/projects/{projectId}/visibility", r => Record(r, project))
            .On(HttpMethod.Delete, $"me/projects/{projectId}", r => Record(r, ""));
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("kofi@example.net", "pw", Ct)).ShouldBeNull();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        (await client.LearningTermsAsync(Ct)).Value!.Count.ShouldBe(4);
        (await client.AddGoalAsync(new GoalDraft("Lead a river walk", null, Guid.Parse(Rivers), new DateOnly(2027, 6, 1), null), Ct)).Value.ShouldNotBeNull();
        (await client.ChangeGoalAsync(Guid.Parse(Walk), new GoalDraft("Lead a river walk", null, null, null, "achieved"), Ct)).Value.ShouldNotBeNull();
        (await client.SetSkillAsync(Guid.Parse(Teamwork), 3, Ct)).Value!.Name.ShouldBe("Teamwork");
        (await client.RemoveSkillAsync(skillId, Ct)).Problem.ShouldBeNull();
        (await client.AddProjectAsync(new ProjectDraft("Mussel map", null, Guid.Parse(Rowing)), Ct)).Value.ShouldNotBeNull();
        (await client.ChangeProjectAsync(projectId, new ProjectDraft("Mussel map", "With a key", Guid.Parse(Rowing)), Ct)).Value.ShouldNotBeNull();
        (await client.SubmitProjectAsync(projectId, Ct)).Value!.Status.ShouldBe("submitted");
        (await client.ShowProjectAsync(projectId, "public", Ct)).Value.ShouldNotBeNull();
        (await client.RemoveProjectAsync(projectId, Ct)).Problem.ShouldBeNull();

        sent.Select(s => $"{s.Method} {s.Path}").ShouldBe([
            "POST /api/v1/me/goals", $"PUT /api/v1/me/goals/{Walk}", "POST /api/v1/me/skills", $"DELETE /api/v1/me/skills/{skillId}",
            "POST /api/v1/me/projects", $"PUT /api/v1/me/projects/{projectId}", $"POST /api/v1/me/projects/{projectId}/submit",
            $"PUT /api/v1/me/projects/{projectId}/visibility", $"DELETE /api/v1/me/projects/{projectId}"]);
        using JsonDocument added = JsonDocument.Parse(sent[0].Body!);
        (added.RootElement.GetProperty("targetTermId").GetString(), added.RootElement.GetProperty("targetDate").GetString()).ShouldBe((Rivers, "2027-06-01"));
        using JsonDocument moved = JsonDocument.Parse(sent[1].Body!);
        moved.RootElement.GetProperty("status").GetString().ShouldBe("achieved");
        using JsonDocument level = JsonDocument.Parse(sent[2].Body!);
        (level.RootElement.GetProperty("termId").GetString(), level.RootElement.GetProperty("level").GetInt32()).ShouldBe((Teamwork, 3));
        using JsonDocument made = JsonDocument.Parse(sent[4].Body!);
        made.RootElement.GetProperty("listingId").GetString().ShouldBe(Rowing);
        using JsonDocument shown = JsonDocument.Parse(sent[7].Body!);
        shown.RootElement.GetProperty("visibility").GetString().ShouldBe("public");
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
