using System.Net;
using System.Text.Json.Nodes;
using BrightTime.Seed;
using BrightTime.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrightTime.Web.Tests;

/// <summary>
/// Passkeys end to end: a software authenticator registers and signs in through the real
/// endpoints, and the server's WebAuthn checks (origin, signature, counter, ownership) run
/// for real.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class PasskeyTests(ConsoleFixture db)
{
    private static readonly Uri Site = new(ConsoleFixture.PublicBaseUrl);
    private static readonly string Origin = Site.GetLeftPart(UriPartial.Authority);

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Admin(string orgSlug) => $"admin@{orgSlug}.example.org";

    [Fact]
    public async Task A_registered_passkey_signs_in_without_a_password()
    {
        RequireDatabase();
        using var key = new SoftwarePasskey(Origin, Site.Host);
        (await RegisterAsync(Admin("kettle-valley-family-center"), key, "Test laptop")).Dispose();

        using Browser browser = db.NewBrowser();
        HttpResponseMessage signedIn = await SignInAsync(browser, key);

        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        JsonNode.Parse(browser.Html)!["redirect"]!.GetValue<string>().ShouldBe("/provider");
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Kettle Valley Family Center");

        await browser.GetAsync("/account/security");
        browser.Html.ShouldContain("Test laptop");
        browser.Html.ShouldContain("last used");
    }

    [Fact]
    public async Task A_passkey_answer_for_another_site_is_refused()
    {
        RequireDatabase();
        using var key = new SoftwarePasskey(Origin, Site.Host);
        (await RegisterAsync(Admin("riverside-youth-center"), key, "Phone")).Dispose();

        using Browser browser = db.NewBrowser();
        (await SignInAsync(browser, key, asOrigin: "https://kettle-brighttime.example.net")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.Html.ShouldContain("didn't sign you in");
    }

    [Fact]
    public async Task A_bad_signature_is_refused()
    {
        RequireDatabase();
        using var key = new SoftwarePasskey(Origin, Site.Host);
        (await RegisterAsync(Admin("eastbank-youth-club"), key, "Key")).Dispose();

        using Browser browser = db.NewBrowser();
        (await SignInAsync(browser, key, tamper: true)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_counter_that_goes_backwards_is_refused_as_a_cloned_key()
    {
        RequireDatabase();
        using var key = new SoftwarePasskey(Origin, Site.Host);
        (await RegisterAsync(Admin("grayhaven-dance-collective"), key, "Key")).Dispose();

        using Browser first = db.NewBrowser();
        key.Counter = 10;
        (await SignInAsync(first, key)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using Browser second = db.NewBrowser();
        key.Counter = 4;
        (await SignInAsync(second, key)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_removed_passkey_can_no_longer_sign_in()
    {
        RequireDatabase();
        string email = Admin("mill-district-makerspace");
        using var key = new SoftwarePasskey(Origin, Site.Host);
        using Browser owner = await RegisterAsync(email, key, "Old phone");

        await owner.GetAsync("/account/security");
        Guid passkeyId = await db.ScalarAsync<Guid>(
            "select id from identity.user_credential where user_id = @u and revoked_at is null",
            ("u", SeedIds.For("user/" + email)));
        await owner.PostAsync("/account/security", $"revoke-{passkeyId:N}");
        owner.Html.ShouldContain("Passkey removed");

        using Browser browser = db.NewBrowser();
        (await SignInAsync(browser, key)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in_with_its_passkey()
    {
        RequireDatabase();
        string email = Admin("kettle-code-club");
        using var key = new SoftwarePasskey(Origin, Site.Host);
        (await RegisterAsync(email, key, "Key")).Dispose();

        await db.ScalarAsync<int>(
            "with u as (update identity.app_user set status = 'suspended' where id = @id returning 1) select count(*)::int from u",
            ("id", SeedIds.For("user/" + email)));

        using Browser browser = db.NewBrowser();
        (await SignInAsync(browser, key)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_answer_cannot_be_replayed()
    {
        RequireDatabase();
        using var key = new SoftwarePasskey(Origin, Site.Host);
        (await RegisterAsync(Admin("kettle-county-swim-club"), key, "Key")).Dispose();

        using Browser browser = db.NewBrowser();
        await browser.GetAsync("/sign-in");
        await browser.PostJsonAsync(PasskeyEndpoints.SignInOptionsPath, new JsonObject());
        JsonObject answer = key.Get(JsonNode.Parse(browser.Html)!);

        (await browser.PostJsonAsync(PasskeyEndpoints.SignInPath, new JsonObject { ["credential"] = answer.DeepClone() })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.PostJsonAsync(PasskeyEndpoints.SignInPath, new JsonObject { ["credential"] = answer.DeepClone() })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "the challenge was used up with the first answer");
    }

    [Fact]
    public async Task Registering_needs_a_signed_in_account_and_the_antiforgery_header()
    {
        RequireDatabase();
        using HttpClient anonymous = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false));
        using var empty = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage noSession = await anonymous.PostAsync(new Uri(PasskeyEndpoints.RegisterOptionsPath, UriKind.Relative), empty, TestContext.Current.CancellationToken);
        noSession.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        noSession.Headers.Location!.ToString().ShouldContain("/sign-in");

        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(Admin("quarry-hill-historical-society"));
        using HttpClient raw = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false));
        using var noToken = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        (await raw.PostAsync(new Uri(PasskeyEndpoints.SignInOptionsPath, UriKind.Relative), noToken, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Registration_started_by_one_account_cannot_be_finished_by_another()
    {
        RequireDatabase();
        using var key = new SoftwarePasskey(Origin, Site.Host);
        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(Admin("kettle-kitchen-collective"));
        await browser.GetAsync("/account/security");
        await browser.PostJsonAsync(PasskeyEndpoints.RegisterOptionsPath, new JsonObject());
        JsonObject answer = key.Create(JsonNode.Parse(browser.Html)!);

        // Same browser, now someone else: the options cookie is bound to the first account.
        await browser.SignInAsync(Admin("money-smart-teens"));
        await browser.GetAsync("/account/security");
        (await browser.PostJsonAsync(PasskeyEndpoints.RegisterPath, new JsonObject { ["name"] = "Hijack", ["credential"] = answer }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await db.ScalarAsync<long>(
            "select count(*) from identity.user_credential where friendly_name = 'Hijack'")).ShouldBe(0);
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Signs in through development sign-in and registers the key. Returns the signed-in browser.</summary>
    private async Task<Browser> RegisterAsync(string email, SoftwarePasskey key, string name)
    {
        Browser browser = db.NewBrowser();
        await browser.SignInAsync(email);
        await browser.GetAsync("/account/security");

        HttpResponseMessage options = await browser.PostJsonAsync(PasskeyEndpoints.RegisterOptionsPath, new JsonObject());
        options.StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        JsonObject answer = key.Create(JsonNode.Parse(browser.Html)!);

        HttpResponseMessage registered = await browser.PostJsonAsync(PasskeyEndpoints.RegisterPath, new JsonObject { ["name"] = name, ["credential"] = answer });
        registered.StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        await browser.GetAsync("/account/security");
        return browser;
    }

    private static async Task<HttpResponseMessage> SignInAsync(Browser browser, SoftwarePasskey key, string? asOrigin = null, bool tamper = false)
    {
        await browser.GetAsync("/sign-in");
        HttpResponseMessage options = await browser.PostJsonAsync(PasskeyEndpoints.SignInOptionsPath, new JsonObject());
        options.StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        JsonObject answer = key.Get(JsonNode.Parse(browser.Html)!, asOrigin, tamper);
        return await browser.PostJsonAsync(PasskeyEndpoints.SignInPath, new JsonObject { ["credential"] = answer });
    }
}
