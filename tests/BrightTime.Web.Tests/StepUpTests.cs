using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Messaging;
using BrightTime.Seed;
using BrightTime.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Step-up (T21): the security page and its actions need a confirmation within the last ten
/// minutes. A movable clock ages the session; everything else is the real pipeline.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed partial class StepUpTests(ConsoleFixture db) : IAsyncDisposable
{
    private const string Password = "lantern orchard river bend";
    private static readonly Uri Site = new(ConsoleFixture.PublicBaseUrl);
    private static readonly TimeSpan PastWindow = TimeSpan.FromMinutes(11);

    private readonly MovableClock _clock = new();
    private WebApplicationFactory<Program>? _app;

    private WebApplicationFactory<Program> App => _app ??= db.Factory.WithWebHostBuilder(b =>
        b.ConfigureServices(s => s.AddSingleton<SharedKernel.Time.IClock>(_clock)));

    private Browser NewBrowser() =>
        new(App.CreateClient(ConsoleFixture.ClientOptions()));

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Admin(string orgSlug) => $"admin@{orgSlug}.example.org";

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_old_sign_in_is_sent_to_confirm_and_a_password_brings_it_back()
    {
        RequireDatabase();
        string email = Admin("kettle-chinese-cultural-association");
        await SetPasswordAsync(email);
        using Browser browser = NewBrowser();
        await browser.SignInAsync(email);
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.OK, "a fresh sign-in needs no confirmation");

        // Revalidating the cookie (every five minutes) keeps the original authentication time...
        _clock.Shift = TimeSpan.FromMinutes(6);
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...so once ten minutes have passed since signing in, the page asks again.
        _clock.Shift = PastWindow;
        HttpResponseMessage old = await browser.GetAsync("/account/security");
        old.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        old.Headers.Location!.ToString().ShouldBe("/account/confirm?returnUrl=%2Faccount%2Fsecurity");

        await browser.GetAsync(old.Headers.Location.ToString());
        HttpResponseMessage confirmed = await browser.PostAsync(old.Headers.Location.ToString(), "confirm-password", ("password", Password));
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        Uri back = confirmed.Headers.Location!;
        (back.IsAbsoluteUri ? back.PathAndQuery : back.ToString()).ShouldBe("/account/security");
        if (back.IsAbsoluteUri)
        {
            back.Host.ShouldBe(ConsoleFixture.Host, "back to this site, never elsewhere");
        }
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_password_does_not_confirm_and_counts_toward_lockout()
    {
        RequireDatabase();
        string email = Admin("kettle-county-workforce-board");
        await SetPasswordAsync(email);
        using Browser browser = NewBrowser();
        await browser.SignInAsync(email);
        _clock.Shift = PastWindow;

        await browser.GetAsync("/account/confirm");
        HttpResponseMessage wrong = await browser.PostAsync("/account/confirm", "confirm-password", ("password", "not it at all, sorry"));

        wrong.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("That password isn");
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<int>("select access_failed_count from identity.app_user where id = @id", ("id", SeedIds.For("user/" + email)))).ShouldBe(1);
    }

    [Fact]
    public async Task A_passkey_confirms_and_only_the_accounts_own_passkey_does()
    {
        RequireDatabase();
        string mine = Admin("kettle-summer-jobs-partnership");
        string theirs = Admin("brannock-chamber-youth-internships");
        using var myKey = new SoftwarePasskey(Site.GetLeftPart(UriPartial.Authority), Site.Host);
        using var theirKey = new SoftwarePasskey(Site.GetLeftPart(UriPartial.Authority), Site.Host);

        using Browser browser = NewBrowser();
        await RegisterPasskeyAsync(browser, mine, myKey);
        using (Browser other = NewBrowser())
        {
            await RegisterPasskeyAsync(other, theirs, theirKey);
        }

        _clock.Shift = PastWindow;
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Someone else's passkey, presented in my session, confirms nothing.
        (await ConfirmWithPasskeyAsync(browser, theirKey)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        HttpResponseMessage confirmed = await ConfirmWithPasskeyAsync(browser, myKey);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        JsonNode.Parse(browser.Html)!["redirect"]!.GetValue<string>().ShouldBe("/account/security");
        (await browser.GetAsync("/account/security")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_script_call_past_the_window_is_told_where_to_confirm()
    {
        RequireDatabase();
        using Browser browser = NewBrowser();
        await browser.SignInAsync(Admin("grayhaven-college-access-network"));
        await browser.GetAsync("/account/security");

        _clock.Shift = PastWindow;
        HttpResponseMessage options = await browser.PostJsonAsync(PasskeyEndpoints.RegisterOptionsPath, new JsonObject());

        options.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonNode.Parse(browser.Html)!["confirm"]!.GetValue<string>().ShouldStartWith("/account/confirm");
    }

    [Fact]
    public async Task Connecting_an_external_account_past_the_window_asks_to_confirm_first()
    {
        RequireDatabase();
        using Browser browser = NewBrowser();
        await browser.SignInAsync(Admin("hollis-bend-community-garden"));
        await browser.GetAsync("/account/security");

        _clock.Shift = PastWindow;
        HttpResponseMessage start = await browser.PostAsync(ExternalSignIn.LinkPath, null, ("provider", FakeOidcProvider.Key));

        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        start.Headers.Location!.ToString().ShouldStartWith("/account/confirm");
        start.Headers.Location.ToString().ShouldNotContain(FakeOidcProvider.Issuer, Case.Sensitive, "the provider flow did not start");
    }

    [Fact]
    public async Task An_account_with_nothing_to_confirm_with_is_told_how_to_get_something()
    {
        RequireDatabase();
        using Browser browser = NewBrowser();
        await browser.SignInAsync(Admin("quarry-hill-wexley-recreation-commission"));
        _clock.Shift = PastWindow;

        await browser.GetAsync("/account/confirm");

        browser.Html.ShouldContain("no password or passkey to confirm with");
        browser.Html.ShouldContain("href=\"/account/forgot\"");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task SetPasswordAsync(string email)
    {
        using Browser browser = NewBrowser();
        int seen = db.Mail.To(email).Count;
        await browser.GetAsync("/account/forgot");
        await browser.PostAsync("/account/forgot", "forgot", ("email", email));
        EmailMessage mail = (await db.Mail.WaitForAsync(email, seen)).ShouldNotBeNull();
        string link = LinkPattern().Match(mail.TextBody).Value;
        string path = new Uri(link).PathAndQuery;
        string token = Uri.UnescapeDataString(new Uri(link).Query.Split("token=")[1]);

        await browser.GetAsync(path);
        (await browser.PostAsync(path, "reset", ("token", token), ("password", Password), ("confirm", Password)))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
    }

    private static async Task RegisterPasskeyAsync(Browser browser, string email, SoftwarePasskey key)
    {
        await browser.SignInAsync(email);
        await browser.GetAsync("/account/security");
        (await browser.PostJsonAsync(PasskeyEndpoints.RegisterOptionsPath, new JsonObject())).StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        JsonObject answer = key.Create(JsonNode.Parse(browser.Html)!);
        (await browser.PostJsonAsync(PasskeyEndpoints.RegisterPath, new JsonObject { ["name"] = "Key", ["credential"] = answer }))
            .StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
    }

    private static async Task<HttpResponseMessage> ConfirmWithPasskeyAsync(Browser browser, SoftwarePasskey key)
    {
        await browser.GetAsync("/account/confirm?returnUrl=%2Faccount%2Fsecurity");
        HttpResponseMessage options = await browser.PostJsonAsync(StepUp.PasskeyOptionsPath, new JsonObject());
        options.StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        JsonObject answer = key.Get(JsonNode.Parse(browser.Html)!);
        return await browser.PostJsonAsync(StepUp.PasskeyPath, new JsonObject { ["credential"] = answer, ["returnUrl"] = "/account/security" });
    }

    [GeneratedRegex(@"https?://\S+/account/reset\?token=\S+")]
    private static partial Regex LinkPattern();

    private sealed class MovableClock : SharedKernel.Time.IClock
    {
        public TimeSpan Shift { get; set; }

        public DateTimeOffset UtcNow => SharedKernel.Time.SystemClock.Instance.UtcNow + Shift;

        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public DateOnly TodayIn(TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, timeZone).DateTime);
    }
}
