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
/// Applying in the app (D72), rendered from a stand-in API as a signed-in parent sees it, and
/// what the client sends when they start, answer and send an application.
/// </summary>
public sealed class ApplyScreenTests
{
    private const string Ada = "a0a00000-0000-0000-0000-0000000000a1";
    private const string ListingId = "1a1a0000-0000-0000-0000-0000000001a1";
    private const string ApplicationId = "a9a90000-0000-0000-0000-0000000009a9";
    private const string WaiverId = "3a3a0000-0000-0000-0000-0000000003a3";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ListingJson(string mode) => $$"""
        {"id":"{{ListingId}}","slug":"rowing-camp","kind":"program","title":"Rowing Camp","summary":"Learn to row.","description":null,
         "contentLanguage":"en","descriptionLanguage":"en","isMachineTranslated":false,
         "organisation":{"id":"0a0a0000-0000-0000-0000-00000000000a","slug":"kettle-rowing","name":"Kettle Rowing"},
         "ageMin":8,"ageMax":14,"gradeMin":null,"gradeMax":null,"costModel":"free","costCentsMin":null,"costCentsMax":null,"slidingScaleNote":null,
         "modality":"in_person","registrationMode":"{{mode}}","externalUrl":null,"mealsProvided":false,"transportProvided":false,"transportNote":null,
         "isDropIn":false,"accessibilityNote":null,"contactEmail":null,"contactPhone":null,"categories":[],"languages":[],"accommodations":[],
         "places":[],"upcoming":[],"deadlines":[]}
        """;

    private const string Rules = """
        [{"id":"7a7a0000-0000-0000-0000-0000000007a1","kind":"age_at_start","config":{"min":8,"max":14},"isHard":true},
         {"id":"7a7a0000-0000-0000-0000-0000000007a2","kind":"grade_range","config":{"min":3,"max":8},"isHard":false}]
        """;

    private const string Household = $$"""
        {"id":"f0f00000-0000-0000-0000-0000000000f0","householdSize":3,"primaryLanguage":"en","needsInterpreter":false,
         "members":[
           {"id":"{{Ada}}","role":"youth","firstName":"Adaeze","lastName":"Okoro","preferredName":"Ada","dateOfBirth":"2014-03-04","grade":6,
            "pronouns":null,"relationship":"daughter","neighbourhoodId":null,"supportNeeds":[],"accountUserId":null},
           {"id":"a0a00000-0000-0000-0000-0000000000a2","role":"guardian","firstName":"Kofi","lastName":"Okoro","preferredName":null,"dateOfBirth":null,"grade":null,
            "pronouns":null,"relationship":null,"neighbourhoodId":null,"supportNeeds":[],"accountUserId":null}],
         "contacts":[]}
        """;

    private static string Application(string status, string eligibility = """{"isEligible":true,"rules":[]}""", string answers = "{}") => $$"""
        {"id":"{{ApplicationId}}","listingId":"{{ListingId}}","listingTitle":"Rowing Camp","organisationId":"0a0a0000-0000-0000-0000-00000000000a",
         "memberId":"{{Ada}}","memberFirstName":"Adaeze","status":"{{status}}","formId":"f1f10000-0000-0000-0000-000000000f1f",
         "answers":{{answers}},"eligibility":{{eligibility}},"startedAt":"2026-10-28T12:00:00Z",
         "submittedAt":{{(status == "draft" ? "null" : "\"2026-10-28T13:00:00Z\"")}},"withdrawnAt":null}
        """;

    private const string Form = """
        {"formId":"f1f10000-0000-0000-0000-000000000f1f","version":1,"hash":"ab12",
         "questions":[
           {"key":"school_name","kind":"short_text","label":"School","help":null,"required":true,"sensitive":false,"choices":null,"maxLength":null,"min":null,"max":null},
           {"key":"household_income","kind":"number","label":"Household income","help":"Roughly is fine.","required":false,"sensitive":true,"choices":null,"maxLength":null,"min":0,"max":null},
           {"key":"tshirt","kind":"single_choice","label":"T-shirt size","help":null,"required":false,"sensitive":false,
            "choices":[{"key":"small","label":"Small"},{"key":"large","label":"Large"}],"maxLength":null,"min":null,"max":null},
           {"key":"photo_release","kind":"consent","label":"Photos are fine.","help":null,"required":true,"sensitive":false,"choices":null,"maxLength":null,"min":null,"max":null}]}
        """;

    private const string Waivers = $$"""
        [{"waiverId":"{{WaiverId}}","title":"Water safety","version":2,"bodyMarkdown":"Rowers wear life jackets.\n\nCoaches watch from a boat.",
          "hash":"c0ffee","isRequired":true,"signedAt":null}]
        """;

    private static FakeApi SignedIn(string kind = "adult") => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]");

    private static FakeApi Draft(string eligibility = """{"isEligible":null,"rules":[{"ruleId":null,"kind":"grade_range","isHard":true,"met":null}]}""") => SignedIn()
        .Json(HttpMethod.Get, $"applications/{ApplicationId}", Application("draft", eligibility, """{"school_name":"PS 12","tshirt":"large"}"""))
        .Json(HttpMethod.Get, $"applications/{ApplicationId}/form", Form)
        .Json(HttpMethod.Get, $"applications/{ApplicationId}/waivers", Waivers);

    [Fact]
    public async Task An_activity_taking_applications_here_offers_Apply_to_a_parent_and_not_to_a_child()
    {
        using FakeApi parent = SignedIn().Json(HttpMethod.Get, "public/listings/rowing-camp", ListingJson("builtin"));
        string html = await RenderAsync<Listing>(parent, new Dictionary<string, object?> { ["Slug"] = "rowing-camp" });
        html.ShouldContain("href=\"listings/rowing-camp/apply\"");
        html.ShouldContain("Apply here, in the app.");

        using FakeApi full = SignedIn().Json(HttpMethod.Get, "public/listings/rowing-camp", ListingJson("waitlist_only"));
        (await RenderAsync<Listing>(full, new Dictionary<string, object?> { ["Slug"] = "rowing-camp" })).ShouldContain("Join the waiting list");

        using FakeApi child = SignedIn("child").Json(HttpMethod.Get, "public/listings/rowing-camp", ListingJson("builtin"));
        string own = await RenderAsync<Listing>(child, new Dictionary<string, object?> { ["Slug"] = "rowing-camp" });
        own.ShouldContain("Ask your parent or guardian to apply for you.");
        own.ShouldNotContain("listings/rowing-camp/apply");
    }

    [Fact]
    public async Task Starting_says_who_the_activity_is_for_and_offers_only_young_members()
    {
        using FakeApi api = SignedIn()
            .Json(HttpMethod.Get, "public/listings/rowing-camp", ListingJson("builtin"))
            .Json(HttpMethod.Get, "public/listings/rowing-camp/eligibility", Rules)
            .Json(HttpMethod.Get, "family-profile", Household);
        string html = await RenderAsync<Apply>(api, new Dictionary<string, object?> { ["Slug"] = "rowing-camp" });

        html.ShouldContain("Aged 8 to 14");
        html.ShouldContain("In Grade 3 to Grade 8 (preferred, not required)");
        Shown(html).ShouldMatch(@"<input type=""radio""[^>]*checked[^>]*>\s*Ada\s*</label>", "the only young member is chosen already");
        html.ShouldNotContain("Kofi", Case.Sensitive, "a guardian doesn't apply to a youth activity");
        html.ShouldNotContain("app.apply");
    }

    [Fact]
    public async Task Starting_without_a_household_sends_the_family_to_add_someone()
    {
        using FakeApi api = SignedIn()
            .Json(HttpMethod.Get, "public/listings/rowing-camp", ListingJson("builtin"))
            .Json(HttpMethod.Get, "public/listings/rowing-camp/eligibility", "[]")
            .On(HttpMethod.Get, "family-profile", _ => FakeApi.Problem(HttpStatusCode.NotFound, "family.no_profile"));
        string html = await RenderAsync<Apply>(api, new Dictionary<string, object?> { ["Slug"] = "rowing-camp" });

        html.ShouldContain("Anyone can apply.");
        html.ShouldContain("Add the child or young person who is applying to your household first.");
        html.ShouldContain("href=\"household/members/add\"");
        html.ShouldNotContain("role=\"alert\"");
    }

    [Fact]
    public async Task A_draft_shows_its_questions_waivers_and_what_will_be_shared()
    {
        using FakeApi api = Draft();
        string html = await RenderAsync<ApplicationDetail>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(ApplicationId) });

        html.ShouldContain("School (required)");
        html.ShouldContain("value=\"PS 12\"");
        html.ShouldContain("Kept private: this answer never appears in any report.");
        html.ShouldContain("Roughly is fine.");
        html.ShouldContain("Large");
        html.ShouldContain("Photos are fine.");
        html.ShouldContain("Water safety");
        html.ShouldContain("<p>Coaches watch from a boat.</p>", Case.Sensitive, "a waiver's text is shown a paragraph at a time, as plain text");
        html.ShouldContain("Type your full name to sign");
        html.ShouldContain("Their name (always shared)");
        Shown(html).ShouldContain("Some of this activity's rules can't be checked for Adaeze yet: School grade.");
        html.ShouldContain("Send the application");
        html.ShouldContain("Delete this draft");
        html.ShouldNotContain("app.apply");
        html.ShouldNotContain("app.applications");
    }

    [Fact]
    public async Task A_draft_failing_a_rule_says_so_and_cannot_be_sent()
    {
        using FakeApi api = Draft("""{"isEligible":false,"rules":[{"ruleId":null,"kind":"age_at_start","isHard":true,"met":false}]}""");
        string html = await RenderAsync<ApplicationDetail>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(ApplicationId) });

        Shown(html).ShouldContain("This activity's rules say Adaeze can't take part: Age.");
        html.ShouldContain("role=\"alert\"");
        html.ShouldMatch("<button type=\"button\" class=\"primary\" disabled[^>]*>Send the application</button>");
    }

    [Fact]
    public async Task An_offered_place_can_be_taken_or_turned_down_and_the_answers_are_shown()
    {
        using FakeApi api = SignedIn()
            .Json(HttpMethod.Get, $"applications/{ApplicationId}", Application("offered", answers: """{"school_name":"PS 12","tshirt":"large","photo_release":true}"""))
            .Json(HttpMethod.Get, $"applications/{ApplicationId}/form", Form);
        string html = await RenderAsync<ApplicationDetail>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(ApplicationId) });

        html.ShouldContain("A place opened up.");
        html.ShouldContain("Take the place");
        html.ShouldContain("Turn it down");
        html.ShouldContain("Withdraw the application");
        html.ShouldContain("<dd>Large</dd>", Case.Sensitive, "a choice is shown by its label");
        html.ShouldContain("<dd>Agreed</dd>");
        html.ShouldNotContain("Send the application");
        api.Requests.ShouldNotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/waivers", StringComparison.Ordinal), "a sent application's waivers are signed already");
    }

    [Fact]
    public async Task The_list_shows_each_application_and_where_it_stands()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "applications", $$"""
            [{"id":"{{ApplicationId}}","listingId":"{{ListingId}}","listingTitle":"Rowing Camp","memberId":"{{Ada}}","memberFirstName":"Adaeze",
              "status":"waitlisted","startedAt":"2026-10-20T12:00:00Z","submittedAt":"2026-10-21T12:00:00Z"}]
            """);
        string html = await RenderAsync<MyApplications>(api);

        html.ShouldContain("Rowing Camp");
        html.ShouldContain($"href=\"applications/{ApplicationId}\"");
        html.ShouldContain("On the waiting list");
        html.ShouldNotContain("app.applications");
    }

    [Fact]
    public async Task Starting_answering_and_sending_send_what_the_routes_expect()
    {
        Dictionary<string, string> sent = [];
        string? key = null;
        HttpResponseMessage Keep(HttpRequestMessage request, string name, string answer)
        {
            // Read here: the client disposes the request's content once it's sent.
            sent[name] = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeApi.Respond(HttpStatusCode.OK, answer);
        }

        using FakeApi api = SignedIn()
            .On(HttpMethod.Post, "applications", r =>
            {
                key = r.Headers.GetValues("Idempotency-Key").Single();
                return Keep(r, "start", Application("draft"));
            })
            .On(HttpMethod.Patch, $"applications/{ApplicationId}", r => Keep(r, "answers", Application("draft")))
            .On(HttpMethod.Post, $"applications/{ApplicationId}/submit", r => Keep(r, "submit", Application("submitted")))
            .On(HttpMethod.Post, $"applications/{ApplicationId}/waivers/{WaiverId}/sign", r => Keep(r, "sign", "{}"));
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("parent@example.net", "pw", Ct)).ShouldBeNull();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        (await client.StartApplicationAsync(Guid.Parse(ListingId), Guid.Parse(Ada), "app-key-0001", Ct)).Value!.IsDraft.ShouldBeTrue();
        (await client.SaveAnswersAsync(Guid.Parse(ApplicationId), new Dictionary<string, object?> { ["school_name"] = "PS 12", ["household_income"] = 42137m, ["tshirt"] = null }, Ct))
            .IsSuccess.ShouldBeTrue();
        (await client.SignWaiverAsync(Guid.Parse(ApplicationId), Guid.Parse(WaiverId), "Kofi Okoro", "c0ffee", Ct)).IsSuccess.ShouldBeTrue();
        (await client.SubmitApplicationAsync(Guid.Parse(ApplicationId), ["member_names", "member_grade"], null, Ct)).Value!.CanWithdraw.ShouldBeTrue();

        key.ShouldBe("app-key-0001");
        using JsonDocument start = JsonDocument.Parse(sent["start"]);
        (start.RootElement.GetProperty("listingId").GetGuid(), start.RootElement.GetProperty("memberId").GetGuid()).ShouldBe((Guid.Parse(ListingId), Guid.Parse(Ada)));
        using JsonDocument answers = JsonDocument.Parse(sent["answers"]);
        JsonElement given = answers.RootElement.GetProperty("answers");
        (given.GetProperty("school_name").GetString(), given.GetProperty("household_income").GetDecimal(), given.GetProperty("tshirt").ValueKind)
            .ShouldBe(("PS 12", 42137m, JsonValueKind.Null), "a null answer clears that question");
        using JsonDocument sign = JsonDocument.Parse(sent["sign"]);
        (sign.RootElement.GetProperty("typedName").GetString(), sign.RootElement.GetProperty("hash").GetString()).ShouldBe(("Kofi Okoro", "c0ffee"));
        using JsonDocument submit = JsonDocument.Parse(sent["submit"]);
        submit.RootElement.GetProperty("shareFields").EnumerateArray().Select(f => f.GetString()).ShouldBe(["member_names", "member_grade"]);
        submit.RootElement.GetProperty("shareExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("\u2068", "", StringComparison.Ordinal).Replace("\u2069", "", StringComparison.Ordinal);

    private static async Task<string> RenderAsync<TComponent>(FakeApi api, IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        await using ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("parent@example.net", "pw", Ct)).ShouldBeNull();
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
