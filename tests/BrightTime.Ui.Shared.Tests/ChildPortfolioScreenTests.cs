using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// A guardian's view of a child profile's portfolio in the app (D93), rendered from a stand-in API:
/// offered from the child's page only while the link is verified and the profile unclaimed; the
/// child's activities, badges and skills with nothing to change; the downloads; and the server's
/// refusal, in words, once the child has claimed their account.
/// </summary>
public sealed class ChildPortfolioScreenTests
{
    private const string ChildId = "c41d0000-0000-0000-0000-000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Child(string kind = "child", string link = "verified") => $$"""
        {"id":"{{ChildId}}","givenName":"Nia","birthMonth":"2018-03","accountKind":"{{kind}}","status":"active",
         "link":{"id":"c41d0000-0000-0000-0000-0000000000aa","relationship":"parent","status":"{{link}}","verificationMethod":"signed_form","verifiedAt":"2026-11-01T12:00:00Z"},
         "consents":[],
         "capabilities":{"isUnder13":true,"hasAccountConsent":true,"canSelfEnroll":false,"canPostInCommunity":false,"canUploadMedia":false,
                         "canPublishShowcase":false,"canReceiveSms":false,"canBeMessagedByOrganisation":false} }
        """;

    private const string Portfolio = """
        {"totalHours":6,
         "participation":[{"id":"d0d00000-0000-0000-0000-000000000001","title":"Pond Club","organisation":"Kettle Parks","startedOn":"2026-10-01","endedOn":null,
                           "sessionsAttended":4,"hours":6,"source":"attendance","isHidden":false,"listingId":null}],
         "badges":[{"id":"a5a50000-0000-0000-0000-000000000001","name":"Pond Dipper","issuer":"Kettle Parks","issuedOn":"2026-11-05T12:00:00Z",
                    "expiresOn":null,"revoked":false,"page":"https://kettle.example.org/badges/pond-dipper-1"}],
         "skills":[{"id":"e5e50000-0000-0000-0000-000000000001","termId":"e5e50000-0000-0000-0000-0000000000aa","name":"Careful looking","level":2,
                    "endorsed":true,"endorsedBy":"Kettle Parks","assertedAt":"2026-11-05T12:00:00Z"}]}
        """;

    private static FakeApi SignedIn() => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me("adult"))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "consent/disclosures", "[]");

    private static Dictionary<string, object?> Id => new() { ["Id"] = Guid.Parse(ChildId) };

    [Fact]
    public async Task A_verified_unclaimed_childs_page_offers_their_portfolio_and_a_claimed_ones_doesnt()
    {
        using FakeApi unclaimed = SignedIn().Json(HttpMethod.Get, $"children/{ChildId}", Child());
        Shown(await RenderAsync<ChildProfile>(unclaimed, Id)).ShouldContain($"href=\"family/{ChildId}/portfolio\">See their portfolio</a>");

        using FakeApi waiting = SignedIn().Json(HttpMethod.Get, $"children/{ChildId}", Child(link: "pending"));
        Shown(await RenderAsync<ChildProfile>(waiting, Id)).ShouldNotContain("See their portfolio", Case.Sensitive, "nothing to see until the link is verified");

        using FakeApi claimed = SignedIn().Json(HttpMethod.Get, $"children/{ChildId}", Child(kind: "youth"));
        Shown(await RenderAsync<ChildProfile>(claimed, Id)).ShouldNotContain("See their portfolio", Case.Sensitive, "a claimed portfolio is the young person's");
    }

    [Fact]
    public async Task The_portfolio_shows_what_they_took_part_in_their_badges_and_skills_with_nothing_to_change()
    {
        using FakeApi api = SignedIn()
            .Json(HttpMethod.Get, $"children/{ChildId}", Child())
            .Json(HttpMethod.Get, $"children/{ChildId}/portfolio", Portfolio);
        string shown = Shown(await RenderAsync<ChildPortfolio>(api, Id));

        shown.ShouldContain("<h1>Nia's portfolio</h1>");
        shown.ShouldContain("Only you can see it.");
        shown.ShouldContain("6 hours in all.");
        shown.ShouldContain("Pond Club");
        shown.ShouldContain("Pond Dipper");
        shown.ShouldContain("From Kettle Parks");
        shown.ShouldContain("Careful looking");
        shown.ShouldContain("Developing");
        shown.ShouldContain("Endorsed by Kettle Parks");
        shown.ShouldContain(">Printable portfolio (PDF)</button>");
        shown.ShouldContain(">Their badges (Open Badges)</button>");
        shown.ShouldContain(">Learner record (CLR)</button>");
        shown.ShouldNotContain(">Hide</button>", Case.Sensitive, "the guardian changes nothing in it");
        shown.ShouldNotContain(">Share</button>");
        shown.ShouldContain($"href=\"family/{ChildId}\">Back to Nia</a>");
        shown.ShouldNotContain("app.child_portfolio.");
        shown.ShouldNotContain("app.portfolio.");
    }

    [Fact]
    public async Task Once_claimed_the_page_says_the_portfolio_is_theirs()
    {
        using FakeApi api = SignedIn()
            .Json(HttpMethod.Get, $"children/{ChildId}", Child(kind: "youth"))
            .On(HttpMethod.Get, $"children/{ChildId}/portfolio", _ => FakeApi.Problem(HttpStatusCode.Forbidden, "portfolio.child_claimed"));
        string shown = Shown(await RenderAsync<ChildPortfolio>(api, Id));

        shown.ShouldContain("Their portfolio is theirs now that they have their own account.");
        shown.ShouldNotContain("Pond Club");
    }

    [Fact]
    public async Task A_download_asks_for_the_childs_file_in_the_chosen_format()
    {
        string? body = null;
        using FakeApi api = SignedIn().On(HttpMethod.Post, $"children/{ChildId}/portfolio/export", r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            HttpResponseMessage file = new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.7")) };
            file.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            file.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "portfolio.pdf" };
            return file;
        });
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();

        DownloadedFile pdf = (await app.GetRequiredService<BrightTimeApi>().ExportChildPortfolioAsync(Guid.Parse(ChildId), "pdf", Ct)).Value!;

        (pdf.ContentType, pdf.FileName).ShouldBe(("application/pdf", "portfolio.pdf"));
        using JsonDocument sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("format").GetString().ShouldBe("pdf");
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
