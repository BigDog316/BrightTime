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
/// The household screens (D71), rendered from a stand-in API as a signed-in parent sees them, and
/// what the client sends when they save.
/// </summary>
public sealed class HouseholdScreenTests
{
    private const string Me = "0b3c8f5e-0000-0000-0000-000000000001";
    private const string Ada = "a0a00000-0000-0000-0000-0000000000a1";
    private const string Kofi = "a0a00000-0000-0000-0000-0000000000a2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Household = $$"""
        {"id":"f0f00000-0000-0000-0000-0000000000f0","householdSize":3,"primaryLanguage":"es","needsInterpreter":true,
         "members":[
           {"id":"{{Ada}}","role":"youth","firstName":"Adaeze","lastName":"Okoro","preferredName":"Ada","dateOfBirth":"2011-03-04","grade":9,
            "pronouns":null,"relationship":"daughter","neighbourhoodId":null,
            "supportNeeds":[{"id":"5a5a0000-0000-0000-0000-00000000005a","kind":"allergy","detail":"Peanuts","shareWithProviders":true}],
            "accountUserId":"{{Me}}"},
           {"id":"{{Kofi}}","role":"guardian","firstName":"Kofi","lastName":"Okoro","preferredName":null,"dateOfBirth":null,"grade":null,
            "pronouns":null,"relationship":null,"neighbourhoodId":null,"supportNeeds":[],"accountUserId":null}],
         "contacts":[{"id":"c0c00000-0000-0000-0000-0000000000c0","name":"Ngozi Eze","relationship":"aunt","phone":"518-555-0100","email":null,"isEmergency":true,"canPickUp":true}]}
        """;

    private static FakeApi SignedIn() => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me("adult"))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "children", "[]");

    [Fact]
    public async Task A_household_not_yet_saved_starts_with_its_details_not_an_error()
    {
        using FakeApi api = SignedIn().On(HttpMethod.Get, "family-profile", _ => FakeApi.Problem(HttpStatusCode.NotFound, "family.no_profile"));
        string html = await RenderAsync<HouseholdHome>(api);

        html.ShouldContain("Tell us about your household once.");
        html.ShouldContain("How many people live in your home?");
        html.ShouldNotContain("role=\"alert\"");
        html.ShouldNotContain("People in your household", Case.Sensitive, "there's nobody to list before the household is saved");
    }

    [Fact]
    public async Task A_saved_household_lists_its_people_with_their_role_and_age_and_its_contacts()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "family-profile", Household);
        string html = await RenderAsync<HouseholdHome>(api);

        html.ShouldContain("Ada Okoro");
        html.ShouldContain("Young person (13 or over), ");
        html.ShouldContain("Parent or guardian");
        html.ShouldContain("Ngozi Eze");
        html.ShouldContain("href=\"household/shares\"");
        html.ShouldContain("<option value=\"es\" selected", Case.Sensitive, "the language saved is the one chosen");
        html.ShouldNotContain("app.household");
    }

    [Fact]
    public async Task A_young_members_page_shows_their_support_needs_and_whose_account_they_are_linked_to()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "family-profile", Household);
        string html = await RenderAsync<HouseholdPerson>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Ada) });

        html.ShouldContain("value=\"Adaeze\"");
        html.ShouldContain("Support needs");
        html.ShouldContain("Peanuts");
        html.ShouldContain("Linked to your own account.");
        html.ShouldContain("Remove from household");
        html.ShouldNotContain("app.household");
    }

    [Fact]
    public async Task An_adult_members_page_asks_no_support_needs_and_offers_no_account()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "family-profile", Household);
        string html = await RenderAsync<HouseholdPerson>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Kofi) });

        html.ShouldContain("value=\"Kofi\"");
        html.ShouldNotContain("Support needs");
        html.ShouldNotContain("Their own account");
    }

    [Fact]
    public async Task Each_share_says_who_and_what_until_when_and_only_a_live_one_can_be_stopped()
    {
        using FakeApi api = SignedIn()
            .Json(HttpMethod.Get, "family-profile", Household)
            .Json(HttpMethod.Get, "family-profile/shares", $$"""
                [{"id":"5b5b0000-0000-0000-0000-0000000005b1","organisationId":"0a0a0000-0000-0000-0000-00000000000a","organisationName":"Kettle Rowing",
                  "listingId":null,"memberIds":["{{Ada}}"],"fields":["member_names","member_birth_dates"],
                  "grantedAt":"2026-09-01T12:00:00Z","expiresAt":null,"revokedAt":null,"status":"active"},
                 {"id":"5b5b0000-0000-0000-0000-0000000005b2","organisationId":"0a0a0000-0000-0000-0000-00000000000b","organisationName":"Harbour Arts",
                  "listingId":null,"memberIds":["{{Ada}}"],"fields":["member_names"],
                  "grantedAt":"2026-06-01T12:00:00Z","expiresAt":null,"revokedAt":"2026-07-01T12:00:00Z","status":"revoked"}]
                """);
        string html = await RenderAsync<HouseholdShares>(api);

        html.ShouldContain("Kettle Rowing");
        html.ShouldContain("Ada");
        html.ShouldContain("names, dates of birth");
        html.ShouldContain("Until you stop it");
        html.ShouldContain("Stopped on");
        CountOf(html, ">Stop sharing</button>").ShouldBe(1, "only the live share can be stopped");
    }

    [Fact]
    public async Task Saving_sends_what_the_routes_expect()
    {
        const string Ife = """
            {"id":"a0a00000-0000-0000-0000-0000000000a3","role":"child","firstName":"Ife","lastName":"Okoro","preferredName":null,"dateOfBirth":"2017-05-06",
             "grade":3,"pronouns":null,"relationship":"son","neighbourhoodId":null,"supportNeeds":[],"accountUserId":null}
            """;
        Dictionary<string, string> sent = [];
        HttpResponseMessage Keep(HttpRequestMessage request, string key, string answer)
        {
            // Read here: the client disposes the request's content once it's sent.
            sent[key] = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeApi.Respond(HttpStatusCode.OK, answer);
        }

        using FakeApi api = SignedIn()
            .On(HttpMethod.Put, "family-profile", r => Keep(r, "household", Household))
            .On(HttpMethod.Post, "family-profile/members", r => Keep(r, "member", Ife))
            .On(HttpMethod.Put, $"family-profile/members/{Ada}/account", r => Keep(r, "link", Ife));
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("parent@example.net", "pw", Ct)).ShouldBeNull();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        (await client.SaveHouseholdAsync(3, "es", true, Ct)).Value!.Members.Count.ShouldBe(2);
        (await client.AddMemberAsync(new MemberDraft("child", "Ife", "Okoro", null, new DateOnly(2017, 5, 6), 3, null, "son", null), Ct)).Value!.FirstName.ShouldBe("Ife");
        (await client.LinkMemberAccountAsync(Guid.Parse(Ada), null, Ct)).IsSuccess.ShouldBeTrue();

        using JsonDocument household = JsonDocument.Parse(sent["household"]);
        (household.RootElement.GetProperty("householdSize").GetInt32(), household.RootElement.GetProperty("primaryLanguage").GetString(),
            household.RootElement.GetProperty("needsInterpreter").GetBoolean()).ShouldBe((3, "es", true));
        using JsonDocument member = JsonDocument.Parse(sent["member"]);
        (member.RootElement.GetProperty("role").GetString(), member.RootElement.GetProperty("dateOfBirth").GetString(), member.RootElement.GetProperty("grade").GetInt32())
            .ShouldBe(("child", "2017-05-06", 3));
        using JsonDocument link = JsonDocument.Parse(sent["link"]);
        link.RootElement.GetProperty("userId").ValueKind.ShouldBe(JsonValueKind.Null, "null unlinks");
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

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
