using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// Downloading a portfolio in the app (D68, D81): the files on offer, the server's file read as
/// it sent it, and the copy handed to the share sheet forgotten when nobody is signed in.
/// </summary>
public sealed class PortfolioDownloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Portfolio = """
        {"displayName":"Kofi R","headline":null,"bio":null,"visibility":"private","publicSlug":null,"totalHours":0,"canShow":true,"participation":[]}
        """;

    private const string Badge = """
        [{"id":"a5a50000-0000-0000-0000-000000000001","name":"River Walker","issuer":"Brannock Parks","issuedOn":"2026-11-05T12:00:00Z",
          "expiresOn":null,"revoked":false,"page":"https://kettle.example.org/badges/river-walker-1"}]
        """;

    private const string Revoked = """
        [{"id":"a5a50000-0000-0000-0000-000000000002","name":"Old Badge","issuer":"Brannock Parks","issuedOn":"2026-01-05T12:00:00Z",
          "expiresOn":null,"revoked":true,"page":"https://kettle.example.org/badges/old-badge-1"}]
        """;

    private static FakeApi SignedIn(string badges) => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me("youth"))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "me/portfolio", Portfolio)
        .Json(HttpMethod.Get, "me/badges", badges)
        .On(HttpMethod.Post, "auth/logout", _ => FakeApi.Respond(HttpStatusCode.NoContent));

    private static HttpResponseMessage File(string body, string type, string name)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = name, FileNameStar = name };
        return response;
    }

    [Fact]
    public async Task With_a_badge_all_three_files_are_offered()
    {
        using FakeApi api = SignedIn(Badge);
        string html = WebUtility.HtmlDecode(await RenderAsync<Portfolio>(api));

        html.ShouldContain("Take it with you");
        html.ShouldContain(">Printable portfolio (PDF)</button>");
        html.ShouldContain(">Your badges (Open Badges)</button>");
        html.ShouldContain(">Learner record (CLR)</button>");
        html.ShouldNotContain("Once you hold a badge");
        html.ShouldNotContain("app.portfolio.download");
    }

    [Fact]
    public async Task Without_a_badge_that_counts_only_the_printable_portfolio_is_offered()
    {
        using FakeApi api = SignedIn(Revoked);
        string html = WebUtility.HtmlDecode(await RenderAsync<Portfolio>(api));

        html.ShouldContain(">Printable portfolio (PDF)</button>");
        html.ShouldNotContain(">Learner record (CLR)</button>", Case.Sensitive, "a revoked badge doesn't go in a learner record");
        html.ShouldNotContain(">Your badges (Open Badges)</button>");
        html.ShouldContain("Once you hold a badge, you can also download it, and a learner record.");
        Pages.Portfolio.Formats([]).ShouldBe(["pdf"]);
    }

    [Fact]
    public async Task The_file_is_read_as_the_server_sent_it_and_a_refusal_is_a_problem()
    {
        string? sentBody = null;
        using FakeApi api = SignedIn(Badge)
            .On(HttpMethod.Post, "me/portfolio/export", r =>
            {
                sentBody = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return sentBody.Contains("clr2", StringComparison.Ordinal)
                    ? FakeApi.Problem(HttpStatusCode.Conflict, "portfolio.nothing_to_export")
                    : File("%PDF-1.7", "application/pdf", "../../Kofi R portfolio.pdf");
            });
        await using ServiceProvider app = await SignedInAppAsync(api);
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        DownloadedFile pdf = (await client.ExportPortfolioAsync("pdf", Ct)).Value!;
        (pdf.ContentType, pdf.FileName, Encoding.UTF8.GetString(pdf.Bytes)).ShouldBe(("application/pdf", "Kofi R portfolio.pdf", "%PDF-1.7"), "no path survives in the name");
        using (JsonDocument body = JsonDocument.Parse(sentBody!))
        {
            body.RootElement.GetProperty("format").GetString().ShouldBe("pdf");
        }

        ApiResult<DownloadedFile> refused = await client.ExportPortfolioAsync("clr2", Ct);
        refused.Problem!.Code.ShouldBe("portfolio.nothing_to_export");
    }

    [Fact]
    public async Task A_shared_file_is_forgotten_on_signing_out_and_at_a_start_with_nobody_signed_in()
    {
        using FakeApi api = SignedIn(Badge);
        await using ServiceProvider app = await SignedInAppAsync(api);
        MemoryFileShare files = (MemoryFileShare)app.GetRequiredService<IFileShare>();

        await files.ShareAsync("Printable portfolio (PDF)", new DownloadedFile([1, 2, 3], "application/pdf", "portfolio.pdf"));
        await app.GetRequiredService<AppSession>().SignOutAsync(Ct);
        files.Shared.ShouldBeEmpty();

        await files.ShareAsync("Printable portfolio (PDF)", new DownloadedFile([1, 2, 3], "application/pdf", "portfolio.pdf"));
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        files.Shared.ShouldBeEmpty("a young person's session isn't kept, so a start finds nobody signed in");
    }

    private static async Task<ServiceProvider> SignedInAppAsync(FakeApi api)
    {
        ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("kofi@example.net", "pw", Ct)).ShouldBeNull();
        return app;
    }

    private static async Task<string> RenderAsync<TComponent>(FakeApi api)
        where TComponent : IComponent
    {
        await using ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("kofi@example.net", "pw", Ct)).ShouldBeNull();
        await using HtmlRenderer renderer = new(app, app.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
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
