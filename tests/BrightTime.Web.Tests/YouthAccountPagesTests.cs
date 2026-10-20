using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.Web.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.Web.Tests;

/// <summary>
/// The web's side of D35: the page a consent email's link opens when no app claims it, the page
/// a claim email's link opens, and the files that let the app claim <c>/app/</c> links.
/// </summary>
/// <remarks>Guardians are the admins of the organisations at sorted indices 40 to 43, used by no other web test.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed partial class YouthAccountPagesTests(ConsoleFixture db)
{
    private const string StrongPassword = "lantern orchard pebble 47";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private static Guid UserOf(string email) => SeedIds.For("user/" + email);

    [Fact]
    public async Task A_guardian_gives_consent_on_the_web_from_their_email_link()
    {
        RequireDatabase();
        string guardian = Admin(40);
        (Guid child, Guid link, string token) = await PendingChildAsync(UserOf(guardian));
        string page = $"/app/guardian-links/{link}/verify";

        // Signed out: where to sign in, coming back here; the script keeps the token meanwhile.
        using (Browser visitor = db.NewBrowser())
        {
            (await visitor.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
            visitor.Html.ShouldContain($"data-consent-link=\"{link}\"");
            visitor.Html.ShouldContain("/sign-in?returnUrl=" + Uri.EscapeDataString(page));
        }

        // Someone else's link says nothing about it.
        using (Browser other = db.NewBrowser())
        {
            await other.SignInAsync(Admin(41));
            await other.GetAsync(page);
            other.Html.ShouldContain("isn&#x27;t for the account you&#x27;re signed in with");
            other.Html.ShouldNotContain("Ada");
        }

        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(guardian);
        await browser.GetAsync(page);
        browser.Html.ShouldContain("Ada");
        // The token field is there, and empty: the script fills it, never the server.
        string input = TokenInput().Match(browser.Html).Value;
        input.ShouldNotBeEmpty();
        input.ShouldNotContain(token);

        // A wrong token (or none, without JavaScript) is refused the way the API refuses it.
        (await browser.PostAsync(page, "consent", ("token", "not-the-token"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("That consent link doesn");

        int seen = db.Mail.To(guardian).Count;
        (await browser.PostAsync(page, "consent", ("token", token))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await browser.GetAsync(page + "?done=1");
        browser.Html.ShouldContain("account is ready to use");
        browser.Html.ShouldContain("data-consent-done=\"true\"");

        (await db.ScalarAsync<string>("select status from identity.guardian_link where id = @l", ("l", link))).ShouldBe("verified");
        (await db.ScalarAsync<string>("select status from identity.app_user where id = @c", ("c", child))).ShouldBe("active");
        (await db.ScalarAsync<string>("select method from identity.parental_consent where child_user_id = @c and scope = 'account'", ("c", child))).ShouldBe("email_plus");
        (await db.Mail.WaitForAsync(guardian, seen)).ShouldNotBeNull("the confirmatory email, the plus of email plus");
    }

    [Fact]
    public async Task A_young_person_claims_their_account_on_the_web_once()
    {
        RequireDatabase();
        Guid child = await ConsentedTeenAsync(UserOf(Admin(42)));
        string own = $"kid-{Guid.NewGuid():N}@example.net";
        AccountClaimIssue issue = await db.Factory.Services.GetRequiredService<PasswordAccounts>()
            .CreateClaimAsync(new TenantId(db.Data.TenantId), new UserId(child), own, Ct);
        string page = "/account/claim?token=" + Uri.EscapeDataString(issue.Claim.ShouldNotBeNull().Token);

        using Browser browser = db.NewBrowser();
        (await browser.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("Make your account your own");

        await browser.PostAsync(page, "claim", ("token", issue.Claim.Token), ("password", StrongPassword), ("confirm", "something else"));
        browser.Html.ShouldContain("don&#x27;t match");

        (await browser.PostAsync(page, "claim", ("token", issue.Claim.Token), ("password", StrongPassword), ("confirm", StrongPassword)))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<long>(
            "select count(*) from identity.app_user where id = @c and account_kind = 'youth' and normalized_email = upper(@e) and email_confirmed",
            ("c", child), ("e", own))).ShouldBe(1);

        // Once.
        await browser.GetAsync(page);
        await browser.PostAsync(page, "claim", ("token", issue.Claim.Token), ("password", StrongPassword), ("confirm", StrongPassword));
        browser.Html.ShouldContain("expired or has already been used");
    }

    [Fact]
    public async Task The_app_link_files_name_the_app_only_once_it_is_configured()
    {
        RequireDatabase();
        using (HttpClient none = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false)))
        {
            (await none.GetAsync(new Uri(AppLinks.AndroidPath, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await none.GetAsync(new Uri(AppLinks.ApplePath, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using WebApplicationFactory<Program> signed = db.Factory.WithWebHostBuilder(b => b
            .UseSetting("AppLinks:AppleAppIds:0", "ABCDE12345.org.brighttime.app")
            .UseSetting("AppLinks:AndroidPackage", "org.brighttime.app")
            .UseSetting("AppLinks:AndroidCertificateFingerprints:0", "AA:BB:CC"));
        using HttpClient client = signed.CreateClient(ConsoleFixture.ClientOptions(cookies: false));

        HttpResponseMessage apple = await client.GetAsync(new Uri(AppLinks.ApplePath, UriKind.Relative), Ct);
        apple.StatusCode.ShouldBe(HttpStatusCode.OK);
        apple.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        JsonElement aasa = JsonDocument.Parse(await apple.Content.ReadAsStringAsync(Ct)).RootElement;
        JsonElement details = aasa.GetProperty("applinks").GetProperty("details")[0];
        details.GetProperty("appIDs")[0].GetString().ShouldBe("ABCDE12345.org.brighttime.app");
        details.GetProperty("components")[0].GetProperty("/").GetString().ShouldBe("/app/*");

        JsonElement assetlinks = JsonDocument.Parse(await client.GetStringAsync(new Uri(AppLinks.AndroidPath, UriKind.Relative), Ct)).RootElement;
        JsonElement target = assetlinks[0].GetProperty("target");
        target.GetProperty("package_name").GetString().ShouldBe("org.brighttime.app");
        target.GetProperty("sha256_cert_fingerprints")[0].GetString().ShouldBe("AA:BB:CC");
    }

    /// <summary>A seven-year-old's profile with a pending link and a live consent-email token, as making it via the app leaves it.</summary>
    private async Task<(Guid Child, Guid Link, string Token)> PendingChildAsync(Guid guardian)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        Guid child = Guid.NewGuid();
        Guid link = Guid.NewGuid();
        await ExecAsync(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, security_stamp, concurrency_stamp, account_kind, status, birth_month, age_band)
            values (@c, @t, 'child-' || @c, upper('child-' || @c), 'stamp', 'concurrency', 'child', 'pending_consent',
                    date_trunc('month', now() - interval '7 years')::date, '6_8');
            insert into identity.app_user_claim (user_id, claim_type, claim_value) values (@c, 'given_name', 'Ada');
            insert into identity.guardian_link (id, tenant_id, guardian_user_id, child_user_id, relationship, status, is_primary,
                                                email_token_hash, email_token_issued_at, email_token_expires_at)
            values (@l, @t, @g, @c, 'parent', 'pending', true, sha256(@s), now(), now() + interval '3 days');
            """,
            ("c", child), ("t", db.Data.TenantId), ("g", guardian), ("l", link), ("s", secret));
        return (child, link, Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    /// <summary>A consented child profile whose owner is now 13 and a few months.</summary>
    private async Task<Guid> ConsentedTeenAsync(Guid guardian)
    {
        Guid child = Guid.NewGuid();
        Guid link = Guid.NewGuid();
        await ExecAsync(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, security_stamp, concurrency_stamp, account_kind, status, birth_month, age_band)
            values (@c, @t, 'child-' || @c, upper('child-' || @c), 'stamp', 'concurrency', 'child', 'active',
                    date_trunc('month', now() - interval '13 years 3 months')::date, '13_14');
            insert into identity.guardian_link (id, tenant_id, guardian_user_id, child_user_id, relationship, status, is_primary, verification_method, verified_at)
            values (@l, @t, @g, @c, 'parent', 'verified', true, 'email_plus', now());
            insert into identity.parental_consent (id, tenant_id, child_user_id, guardian_user_id, guardian_link_id, scope, method, consent_text_hash)
            values (gen_random_uuid(), @t, @c, @g, @l, 'account', 'email_plus', sha256('test'::bytea));
            """,
            ("c", child), ("t", db.Data.TenantId), ("g", guardian), ("l", link));
        return child;
    }

    [GeneratedRegex("<input[^>]*name=\"token\"[^>]*>")]
    private static partial Regex TokenInput();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test setup written as literals; values are bound parameters.")]
    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(db.Connection);
        await connection.OpenAsync(Ct);
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}
