using System.Net;
using BrightTime.Seed;
using BrightTime.Web.Auth;

namespace BrightTime.Web.Tests;

/// <summary>
/// External OpenID Connect sign-in (decision D17), against <see cref="FakeOidcProvider"/>.
/// The rules under test: an external account signs in only to an account it was connected
/// to; connecting needs the person signed in; email never connects anything.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class ExternalSignInTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Admin(string orgSlug) => $"admin@{orgSlug}.example.org";

    private static string NewSubject() => "subject-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task An_external_account_that_is_not_connected_signs_in_to_nothing()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();

        Uri done = await SignInExternallyAsync(browser, NewSubject());

        done.ToString().ShouldStartWith("/sign-in?external=unconnected");
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.Redirect, "nobody is signed in");
        (await browser.GetAsync(done.ToString())).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("isn&#x27;t connected to a BrightTime account here");
    }

    [Fact]
    public async Task A_connected_external_account_signs_in_to_its_account()
    {
        RequireDatabase();
        string subject = NewSubject();
        (await ConnectAsync(Admin("kettle-county-youth-council"), subject)).ShouldBe("/account/security?external=connected");

        using Browser browser = db.NewBrowser();
        Uri done = await SignInExternallyAsync(browser, subject);

        done.ToString().ShouldBe("/provider");
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Kettle County Youth Council");
    }

    [Fact]
    public async Task An_email_claim_never_connects_an_account()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();

        // A provider vouching for an existing person's address is still not that person.
        Uri done = await SignInExternallyAsync(browser, NewSubject(), email: Admin("kettleton-debate-league"));

        done.ToString().ShouldStartWith("/sign-in?external=unconnected");
        (await db.ScalarAsync<long>(
            "select count(*) from identity.app_user_login where user_id = @u",
            ("u", SeedIds.For("user/" + Admin("kettleton-debate-league"))))).ShouldBe(0);
    }

    [Fact]
    public async Task A_connection_finished_under_a_different_sign_in_is_refused()
    {
        RequireDatabase();
        string subject = NewSubject();
        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(Admin("first-generation-scholars-of-kettle-county"));
        await browser.GetAsync("/account/security");
        HttpResponseMessage start = await browser.PostAsync(ExternalSignIn.LinkPath, null, ("provider", FakeOidcProvider.Key));
        string callback = db.Idp.Authorize(start.Headers.Location!, subject);

        // Someone else signs in on this browser before the provider sends it back.
        await browser.SignInAsync(Admin("kettle-interfaith-youth-network"));
        Uri done = await FollowCallbackAsync(browser, callback);

        done.ToString().ShouldBe("/account/security?external=wrong_session");
        (await db.ScalarAsync<long>("select count(*) from identity.app_user_login where provider_key = @s", ("s", subject))).ShouldBe(0);
    }

    [Fact]
    public async Task An_external_account_connects_to_one_BrightTime_account_only()
    {
        RequireDatabase();
        string subject = NewSubject();
        (await ConnectAsync(Admin("mindful-youth-kettle-county"), subject)).ShouldBe("/account/security?external=connected");

        (await ConnectAsync(Admin("kettle-teen-health-collaborative"), subject)).ShouldBe("/account/security?external=in_use");
    }

    [Fact]
    public async Task A_disconnected_account_no_longer_signs_in()
    {
        RequireDatabase();
        string email = Admin("st-brendan-parish-youth-ministry");
        string subject = NewSubject();
        (await ConnectAsync(email, subject)).ShouldBe("/account/security?external=connected");

        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(email);
        await owner.GetAsync("/account/security");
        owner.Html.ShouldContain("Connected");
        await owner.PostAsync("/account/security", $"disconnect-{FakeOidcProvider.Key}");
        owner.Html.ShouldContain("Disconnected");

        using Browser browser = db.NewBrowser();
        (await SignInExternallyAsync(browser, subject)).ToString().ShouldStartWith("/sign-in?external=unconnected");
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-nonce")]
    public async Task A_token_that_fails_validation_signs_in_nobody(string fault)
    {
        RequireDatabase();
        string subject = NewSubject();
        (await ConnectAsync(Admin("brannock-islamic-center-youth-circle"), subject)).ShouldNotBeNull();

        using Browser browser = db.NewBrowser();
        Uri done = await SignInExternallyAsync(browser, subject,
            wrongKey: fault == "wrong-key",
            wrongIssuer: fault == "wrong-issuer" ? "https://impostor.example.test" : null,
            wrongNonce: fault == "wrong-nonce" ? "not-the-nonce" : null);

        done.ToString().ShouldBe("/sign-in?external=failed");
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task A_callback_carried_to_another_browser_is_refused()
    {
        RequireDatabase();
        string subject = NewSubject();
        (await ConnectAsync(Admin("bangladeshi-american-youth-alliance"), subject)).ShouldNotBeNull();

        // The victim's browser starts the flow; an attacker replays the callback in theirs,
        // without the state's correlation cookie.
        using Browser victim = db.NewBrowser();
        await victim.GetAsync("/sign-in");
        HttpResponseMessage start = await victim.PostAsync(ExternalSignIn.StartPath, null, ("provider", FakeOidcProvider.Key));
        string callback = db.Idp.Authorize(start.Headers.Location!, subject);

        using Browser attacker = db.NewBrowser();
        (await FollowCallbackAsync(attacker, callback)).ToString().ShouldBe("/sign-in?external=failed");
    }

    [Fact]
    public async Task Starting_a_flow_needs_the_antiforgery_token_and_a_known_provider()
    {
        RequireDatabase();
        using HttpClient raw = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false));
        using var form = new FormUrlEncodedContent([new("provider", FakeOidcProvider.Key)]);
        (await raw.PostAsync(new Uri(ExternalSignIn.StartPath, UriKind.Relative), form, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using Browser browser = db.NewBrowser();
        await browser.GetAsync("/sign-in");
        (await browser.PostAsync(ExternalSignIn.StartPath, null, ("provider", "nonexistent"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Signs in as the account and connects the external subject. Returns where the app lands.</summary>
    private async Task<string> ConnectAsync(string email, string subject)
    {
        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(email);
        await browser.GetAsync("/account/security");
        HttpResponseMessage start = await browser.PostAsync(ExternalSignIn.LinkPath, null, ("provider", FakeOidcProvider.Key));
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        return (await FollowCallbackAsync(browser, db.Idp.Authorize(start.Headers.Location!, subject))).ToString();
    }

    private async Task<Uri> SignInExternallyAsync(
        Browser browser, string subject, string? email = null, bool wrongKey = false, string? wrongIssuer = null, string? wrongNonce = null)
    {
        await browser.GetAsync("/sign-in");
        browser.Html.ShouldContain($"Sign in with {FakeOidcProvider.DisplayName}");
        HttpResponseMessage start = await browser.PostAsync(ExternalSignIn.StartPath, null, ("provider", FakeOidcProvider.Key));
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        start.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(FakeOidcProvider.Issuer + "/authorize");

        string callback = db.Idp.Authorize(start.Headers.Location!, subject, email, wrongKey, wrongIssuer, wrongNonce);
        return await FollowCallbackAsync(browser, callback);
    }

    /// <summary>The provider's redirect back, through the handler to the app's finish step. Returns the final destination.</summary>
    private static async Task<Uri> FollowCallbackAsync(Browser browser, string callback)
    {
        HttpResponseMessage handled = await browser.GetAsync(new Uri(callback).PathAndQuery);
        handled.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        Uri next = handled.Headers.Location!;
        if (next.ToString() != ExternalSignIn.FinishPath)
        {
            return next;
        }

        HttpResponseMessage finished = await browser.GetAsync(next.ToString());
        finished.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        return finished.Headers.Location!;
    }
}
