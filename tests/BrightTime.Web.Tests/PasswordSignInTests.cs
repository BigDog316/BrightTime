using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Messaging;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Password sign-in and reset (T21), through the real pipeline. Seeded accounts have no
/// password, so each test gives one a password the way a person would: through the reset email.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed partial class PasswordSignInTests(ConsoleFixture db)
{
    private const string NewPassword = "correct horse kettle staple";

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Admin(string orgSlug) => $"admin@{orgSlug}.example.org";

    [Fact]
    public async Task A_reset_link_sets_a_password_that_then_signs_in()
    {
        RequireDatabase();
        string email = Admin("kettle-county-public-library");
        using Browser browser = db.NewBrowser();

        string link = await RequestResetAsync(browser, email);
        link.ShouldStartWith(ConsoleFixture.PublicBaseUrl + "account/reset?token=");

        await ResetAsync(browser, link, NewPassword).ShouldBeAsync(HttpStatusCode.Redirect);

        await SignInAsync(browser, email, NewPassword).ShouldBeAsync(HttpStatusCode.Redirect);
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Kettle County Public Library");
    }

    [Fact]
    public async Task A_reset_link_works_once()
    {
        RequireDatabase();
        string email = Admin("grayhaven-recreation-department");
        using Browser browser = db.NewBrowser();

        string link = await RequestResetAsync(browser, email);
        await ResetAsync(browser, link, NewPassword).ShouldBeAsync(HttpStatusCode.Redirect);

        await ResetAsync(browser, link, "a different long passphrase").ShouldBeAsync(HttpStatusCode.OK);
        browser.Html.ShouldContain("expired or has already been used");
    }

    [Fact]
    public async Task An_unknown_address_gets_the_same_answer_and_no_email()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();

        await browser.GetAsync("/account/forgot");
        HttpResponseMessage unknown = await browser.PostAsync("/account/forgot", "forgot", ("email", "nobody@nowhere.example.org"));
        await browser.GetAsync("/account/forgot");
        HttpResponseMessage known = await browser.PostAsync("/account/forgot", "forgot", ("email", Admin("brannock-parks-and-recreation")));

        unknown.StatusCode.ShouldBe(known.StatusCode);
        unknown.Headers.Location.ShouldBe(known.Headers.Location);

        (await db.Mail.WaitForAsync(Admin("brannock-parks-and-recreation"))).ShouldNotBeNull();
        db.Mail.To("nobody@nowhere.example.org").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_forged_host_header_cannot_redirect_the_reset_link()
    {
        RequireDatabase();
        string email = Admin("cedar-run-nature-center");
        using HttpClient client = db.Factory.CreateClient(ConsoleFixture.ClientOptions());
        client.DefaultRequestHeaders.Host = "attacker.example.net";
        int seen = db.Mail.To(email).Count;

        // A host that is no tenant's never reaches the page, so no link can be made for it.
        using HttpResponseMessage page = await client.GetAsync(new Uri("/account/forgot", UriKind.Relative), TestContext.Current.CancellationToken);
        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var form = new FormUrlEncodedContent([new("email", email)]);
        using HttpResponseMessage post = await client.PostAsync(new Uri("/account/forgot", UriKind.Relative), form, TestContext.Current.CancellationToken);
        post.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And on the tenant's own host, the link is the tenant's address from its domain record.
        using Browser browser = db.NewBrowser();
        string link = await RequestResetAsync(browser, email);
        link.ShouldStartWith(ConsoleFixture.PublicBaseUrl);
        db.Mail.To(email).Count.ShouldBe(seen + 1);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        RequireDatabase();
        string email = Admin("lakeview-paddling-club");
        using Browser browser = db.NewBrowser();
        await ResetAsync(browser, await RequestResetAsync(browser, email), NewPassword);

        for (int i = 0; i < 5; i++)
        {
            await SignInAsync(browser, email, "not the password at all");
            browser.Html.ShouldContain("don't match an account that can sign in");
        }

        HttpResponseMessage right = await SignInAsync(browser, email, NewPassword);
        right.StatusCode.ShouldBe(HttpStatusCode.OK, "locked: the right password is refused too");
        browser.Html.ShouldContain("don't match an account that can sign in", Case.Sensitive, "and the message is the same as for a wrong one");

        (await db.ScalarAsync<bool>(
            "select lockout_end > now() from identity.app_user where id = @id",
            ("id", SeedIds.For("user/" + email)))).ShouldBeTrue();
    }

    [Fact]
    public async Task An_account_without_a_password_and_an_unknown_one_fail_alike()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();

        await SignInAsync(browser, Admin("marrow-point-soccer-league"), NewPassword);
        string noPassword = Message(browser.Html);
        await SignInAsync(browser, "nobody@nowhere.example.org", NewPassword);
        string unknown = Message(browser.Html);

        noPassword.ShouldNotBeEmpty();
        noPassword.ShouldBe(unknown);
    }

    [Fact]
    public async Task A_weak_or_mismatched_new_password_is_refused_with_a_reason()
    {
        RequireDatabase();
        string email = Admin("wexley-track-club");
        using Browser browser = db.NewBrowser();
        string link = await RequestResetAsync(browser, email);

        await ResetAsync(browser, link, "short");
        browser.Html.ShouldContain("at least 12 characters");

        await ResetAsync(browser, link, "password1234");
        browser.Html.ShouldContain("too common");

        string path = new Uri(link).PathAndQuery;
        await browser.GetAsync(path);
        await browser.PostAsync(path, "reset", ("token", Token(link)), ("password", NewPassword), ("confirm", NewPassword + "x"));
        browser.Html.ShouldContain("don&#x27;t match");

        // None of those used up the link.
        await ResetAsync(browser, link, NewPassword).ShouldBeAsync(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Sign_in_attempts_are_rate_limited_per_client()
    {
        RequireDatabase();
        await using WebApplicationFactory<Program> strict = db.Factory.WithWebHostBuilder(b => b.UseSetting("Auth:SignInAttemptsPerMinute", "2"));
        using var browser = new Browser(strict.CreateClient(ConsoleFixture.ClientOptions()));

        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 3; i++)
        {
            statuses.Add((await SignInAsync(browser, "nobody@nowhere.example.org", "whatever whatever")).StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task A_password_reset_ends_existing_sessions()
    {
        RequireDatabase();
        string email = Admin("orrin-valley-martial-arts-academy");
        var clock = new MovableClock();
        await using WebApplicationFactory<Program> app = db.Factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<SharedKernel.Time.IClock>(clock)));

        using var signedIn = new Browser(app.CreateClient(ConsoleFixture.ClientOptions()));
        await signedIn.SignInAsync(email);
        (await signedIn.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var elsewhere = new Browser(app.CreateClient(ConsoleFixture.ClientOptions()));
        await ResetAsync(elsewhere, await RequestResetAsync(elsewhere, email), NewPassword).ShouldBeAsync(HttpStatusCode.Redirect);

        clock.Shift = Auth.ConsoleServices.RevalidateAfter + TimeSpan.FromMinutes(1);
        HttpResponseMessage after = await signedIn.GetAsync("/provider");
        after.StatusCode.ShouldBe(HttpStatusCode.Redirect, "the old session's security stamp no longer matches");
        after.Headers.Location!.ToString().ShouldContain("/sign-in");
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Asks for a reset and returns the link from the email.</summary>
    private async Task<string> RequestResetAsync(Browser browser, string email)
    {
        int seen = db.Mail.To(email).Count;
        await browser.GetAsync("/account/forgot");
        (await browser.PostAsync("/account/forgot", "forgot", ("email", email))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        EmailMessage mail = (await db.Mail.WaitForAsync(email, seen)).ShouldNotBeNull("the reset email was not sent");
        return LinkPattern().Match(mail.TextBody).Value;
    }

    private static async Task<HttpResponseMessage> ResetAsync(Browser browser, string link, string password)
    {
        string path = new Uri(link).PathAndQuery;
        await browser.GetAsync(path);
        return await browser.PostAsync(path, "reset", ("token", Token(link)), ("password", password), ("confirm", password));
    }

    private static async Task<HttpResponseMessage> SignInAsync(Browser browser, string email, string password)
    {
        await browser.GetAsync("/sign-in");
        return await browser.PostAsync("/sign-in", "sign-in", ("email", email), ("password", password));
    }

    private static string Token(string link) =>
        Uri.UnescapeDataString(new Uri(link).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("token=", StringComparison.Ordinal))[6..]);

    private static string Message(string html) => ErrorPattern().Match(html).Groups[1].Value.Trim();

    [GeneratedRegex(@"https?://\S+/account/reset\?token=\S+")]
    private static partial Regex LinkPattern();

    [GeneratedRegex("id=\"sign-in-error\">(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex ErrorPattern();

    private sealed class MovableClock : SharedKernel.Time.IClock
    {
        public TimeSpan Shift { get; set; }

        public DateTimeOffset UtcNow => SharedKernel.Time.SystemClock.Instance.UtcNow + Shift;

        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public DateOnly TodayIn(TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, timeZone).DateTime);
    }
}

internal static class StatusAssertions
{
    public static async Task ShouldBeAsync(this Task<HttpResponseMessage> response, HttpStatusCode expected) =>
        (await response).StatusCode.ShouldBe(expected);
}
