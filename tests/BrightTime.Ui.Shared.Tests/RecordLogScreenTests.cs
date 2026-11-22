using System.Net;
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
/// "Who has seen my record" in the app (D96), rendered from a stand-in API: offered from the
/// account menu, every entry worded (staff by organisation and role, a refusal marked, the basis
/// given), older entries fetched by the cursor, and for a guardian, offered from a verified
/// child's page and refused, in words, once the child has their own account.
/// </summary>
public sealed class RecordLogScreenTests
{
    private const string ChildId = "c41d0000-0000-0000-0000-000000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string First = """
        {"entries":[
           {"at":"2026-11-20T15:00:00Z","what":"record_read","refused":true,"who":"staff","organisation":"Riverside Youth Center","role":"owner","title":null,"basis":null},
           {"at":"2026-11-19T15:00:00Z","what":"application_decided","refused":false,"who":"staff","organisation":"Kettle Parks","role":"staff","title":"Youth Coordinator","basis":null},
           {"at":"2026-11-18T15:00:00Z","what":"record_read","refused":false,"who":"staff","organisation":"Kettle Parks","role":null,"title":null,"basis":"enrollment"},
           {"at":"2026-11-17T15:00:00Z","what":"link_verified","refused":false,"who":"organiser","organisation":null,"role":null,"title":null,"basis":null},
           {"at":"2026-11-16T15:00:00Z","what":"portfolio_public_read","refused":false,"who":"public","organisation":null,"role":null,"title":null,"basis":null}],
         "older":"cursor-1"}
        """;

    private const string Second = """
        {"entries":[{"at":"2026-11-01T15:00:00Z","what":"family_changed","refused":false,"who":"guardian","organisation":null,"role":null,"title":null,"basis":null}],"older":null}
        """;

    private static string Child(string kind = "child") => $$"""
        {"id":"{{ChildId}}","givenName":"Ada","birthMonth":"2018-03","accountKind":"{{kind}}","status":"active",
         "link":{"id":"c41d0000-0000-0000-0000-0000000000ab","relationship":"parent","status":"verified","verificationMethod":"signed_form","verifiedAt":"2026-11-01T12:00:00Z"},
         "consents":[],
         "capabilities":{"isUnder13":true,"hasAccountConsent":true,"canSelfEnroll":false,"canPostInCommunity":false,"canUploadMedia":false,
                         "canPublishShowcase":false,"canReceiveSms":false,"canBeMessagedByOrganisation":false} }
        """;

    private static FakeApi SignedIn(string kind = "youth") => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "consent/disclosures", "[]");

    private static Dictionary<string, object?> Id => new() { ["Id"] = Guid.Parse(ChildId) };

    [Fact]
    public async Task The_account_menu_offers_it()
    {
        using FakeApi api = SignedIn();
        Shown(await RenderAsync<Account>(api, new Dictionary<string, object?>())).ShouldContain("href=\"account/record-log\">Who has seen your record</a>");
    }

    [Fact]
    public async Task Each_entry_says_who_what_why_and_when_and_a_refusal_is_marked()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/record-log", First);
        string shown = Shown(await RenderAsync<RecordLog>(api, new Dictionary<string, object?>()));

        shown.ShouldContain("<h1>Who has seen your record</h1>");
        shown.ShouldContain("Things you did yourself aren't listed.");
        shown.ShouldContain("Riverside Youth Center (owner)");
        shown.ShouldContain("<strong>Turned away:</strong> Looked at the record");
        shown.ShouldContain("Youth Coordinator at Kettle Parks (staff)");
        shown.ShouldContain("Made a decision on an application");
        shown.ShouldContain("Kettle Parks (no longer there)");
        shown.ShouldContain("Because of an enrolment");
        shown.ShouldContain("The site's organisers");
        shown.ShouldContain("Checked a guardian link");
        shown.ShouldContain("Someone viewing the public page");
        shown.ShouldContain("datetime=\"2026-11-20T15:00:00.0000000+00:00\"");
        shown.ShouldContain(">Show older</button>");
        shown.ShouldNotContain("app.record_log.");
    }

    [Fact]
    public async Task Older_entries_are_fetched_by_the_cursor()
    {
        using FakeApi api = SignedIn()
            .On(HttpMethod.Get, "me/record-log", r => FakeApi.Respond(HttpStatusCode.OK, r.RequestUri!.Query == "?older=cursor-1" ? Second : First));
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        RecordLogPage first = (await client.RecordLogAsync(null, Ct)).Value!;
        RecordLogPage older = (await client.RecordLogAsync(first.Older, Ct)).Value!;

        (first.Entries.Count, first.Older).ShouldBe((5, "cursor-1"));
        (older.Entries.Single().What, older.Older).ShouldBe(("family_changed", (string?)null));
    }

    [Fact]
    public async Task A_guardian_reaches_a_childs_log_from_their_page_until_the_child_claims_it()
    {
        using FakeApi page = SignedIn("adult").Json(HttpMethod.Get, $"children/{ChildId}", Child());
        Shown(await RenderAsync<ChildProfile>(page, Id)).ShouldContain($"href=\"family/{ChildId}/record-log\">Who has seen their record</a>");

        using FakeApi log = SignedIn("adult")
            .Json(HttpMethod.Get, $"children/{ChildId}", Child())
            .Json(HttpMethod.Get, $"children/{ChildId}/record-log", Second);
        string shown = Shown(await RenderAsync<ChildRecordLog>(log, Id));
        shown.ShouldContain("<h1>Who has seen Ada's record</h1>");
        shown.ShouldContain("Every time someone other than you looked at or changed Ada's record");
        shown.ShouldContain("A guardian");
        shown.ShouldContain("Changed the family profile");
        shown.ShouldNotContain(">Show older</button>", Case.Sensitive, "there is nothing older");
        shown.ShouldContain($"href=\"family/{ChildId}\">Back to Ada</a>");

        using FakeApi claimed = SignedIn("adult")
            .Json(HttpMethod.Get, $"children/{ChildId}", Child(kind: "youth"))
            .On(HttpMethod.Get, $"children/{ChildId}/record-log", _ => FakeApi.Problem(HttpStatusCode.Forbidden, "record_log.child_claimed"));
        string refused = Shown(await RenderAsync<ChildRecordLog>(claimed, Id));
        refused.ShouldContain("Their record is theirs now that they have their own account.");
        refused.ShouldNotContain("Changed the family profile");
    }

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static async Task<string> RenderAsync<TComponent>(FakeApi api, IReadOnlyDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        await using ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();
        await using HtmlRenderer renderer = new(app, app.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(new Dictionary<string, object?>(parameters)));
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
