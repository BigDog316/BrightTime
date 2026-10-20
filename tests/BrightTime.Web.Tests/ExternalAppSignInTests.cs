using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// External sign-in started from the app (D41): the provider's flow runs here as on the web, and
/// ends with a one-time code handed to the app's scheme, bound to the app's PKCE challenge. As on
/// the web (D17), only an account already connected to the provider gets one.
/// </summary>
/// <remarks>The admin at sorted index 46, used by no other web test.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ExternalAppSignInTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private static string NewSubject() => "app-" + Guid.NewGuid().ToString("N");

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    [Fact]
    public async Task A_connected_account_gets_a_code_only_the_starting_app_can_redeem()
    {
        RequireDatabase();
        string email = Admin(46);
        string subject = NewSubject();
        await ConnectAsync(email, subject);
        (string verifier, string challenge) = Pkce();
        const string State = "state-from-the-app-0123456789";

        Uri handed = await StartFromAppAsync(challenge, State, subject);

        handed.Scheme.ShouldBe("org.brighttime.app");
        Dictionary<string, string> values = Fragment(handed);
        values["state"].ShouldBe(State);
        values.ShouldNotContainKey("error");

        AppSignInCodes codes = db.Factory.Services.GetRequiredService<AppSignInCodes>();
        TenantId tenant = new(db.Data.TenantId);
        (await codes.RedeemAsync(tenant, values["code"], Pkce().Verifier, Ct)).ShouldBeNull("another app's verifier");
        (await codes.RedeemAsync(tenant, values["code"], verifier, Ct)).ShouldBeNull("spent by the failed attempt: a code is tried once");

        // A fresh flow, redeemed by the app that started it.
        Uri again = await StartFromAppAsync(challenge, State, subject);
        (UserId User, string Method)? redeemed = await codes.RedeemAsync(tenant, Fragment(again)["code"], verifier, Ct);
        redeemed.ShouldNotBeNull();
        redeemed.Value.User.Value.ShouldBe(SeedIds.For("user/" + email));
        redeemed.Value.Method.ShouldBe("external");
    }

    [Fact]
    public async Task An_unconnected_account_gets_no_code_and_a_malformed_start_is_refused()
    {
        RequireDatabase();
        (_, string challenge) = Pkce();
        Uri handed = await StartFromAppAsync(challenge, "another-state-0123456789", NewSubject());
        Dictionary<string, string> values = Fragment(handed);
        values["error"].ShouldBe("unconnected");
        values.ShouldNotContainKey("code");

        using HttpClient raw = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false));
        (await raw.GetAsync(new Uri($"/sign-in/external/app/{FakeOidcProvider.Key}?challenge=short&state=another-state-0123456789", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await raw.GetAsync(new Uri($"/sign-in/external/app/{FakeOidcProvider.Key}?challenge={challenge}&state=x", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await raw.GetAsync(new Uri($"/sign-in/external/app/nonexistent?challenge={challenge}&state=another-state-0123456789", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>The app's flow in a fresh browser, as the system browser runs it. Returns where it hands back.</summary>
    private async Task<Uri> StartFromAppAsync(string challenge, string state, string subject)
    {
        using Browser browser = db.NewBrowser();
        HttpResponseMessage start = await browser.GetAsync($"/sign-in/external/app/{FakeOidcProvider.Key}?challenge={challenge}&state={state}");
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        string callback = db.Idp.Authorize(start.Headers.Location!, subject);
        HttpResponseMessage handled = await browser.GetAsync(new Uri(callback).PathAndQuery);
        handled.Headers.Location!.ToString().ShouldBe(ExternalSignIn.FinishPath);
        HttpResponseMessage finished = await browser.GetAsync(ExternalSignIn.FinishPath);
        finished.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return finished.Headers.Location!;
    }

    private async Task ConnectAsync(string email, string subject)
    {
        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(email);
        await browser.GetAsync("/account/security");
        HttpResponseMessage start = await browser.PostAsync(ExternalSignIn.LinkPath, null, ("provider", FakeOidcProvider.Key));
        string callback = db.Idp.Authorize(start.Headers.Location!, subject);
        await browser.GetAsync(new Uri(callback).PathAndQuery);
        (await browser.GetAsync(ExternalSignIn.FinishPath)).Headers.Location!.ToString().ShouldBe("/account/security?external=connected");
    }

    private static Dictionary<string, string> Fragment(Uri handed) =>
        handed.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
