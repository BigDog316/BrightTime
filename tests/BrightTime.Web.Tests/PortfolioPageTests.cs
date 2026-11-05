using System.Net;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Web.Tests;

/// <summary>
/// The pages a young person's links open (D77): a shown portfolio, and a badge check. Anyone can
/// open them without signing in; neither ever names the young person beyond the display name they
/// chose, and a portfolio that isn't shown is indistinguishable from one that never existed.
/// </summary>
/// <remarks>
/// The young people are accounts made here, and the badges are written straight to the tables:
/// what's under test is the page, not issuing (BadgeApiTests covers that).
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class PortfolioPageTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    /// <summary>A young person's account with a portfolio shown as asked, one record shown and one hidden.</summary>
    private async Task<(Guid User, string Slug)> PortfolioAsync(string visibility)
    {
        Guid user = UuidV7.NewId();
        string slug = $"kofi-{user.ToString("N")[^10..]}";
        string email = $"portfolio-{user:N}@example.org";
        await db.ScalarAsync<int>(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email,
                                           security_stamp, concurrency_stamp, account_kind, age_band)
            values (@u, @t, @e, upper(@e), @e, upper(@e), 'stamp', 'concurrency', 'youth', '15_17');
            insert into learning.learner_profile (user_id, tenant_id, display_name, headline, bio, visibility, public_slug)
            values (@u, @t, 'Kofi R', 'Rower and chess player', 'I row on the river.' || chr(10) || 'And I play chess.', @v, @s);
            insert into learning.participation_record (id, tenant_id, user_id, title_snapshot, org_name_snapshot, started_on, ended_on, sessions_attended, hours, source, is_hidden)
            values (@r1, @t, @u, 'Junior Rowing', 'Kettle Rowing Club', date '2026-09-01', null, 5, 12.5, 'attendance', false),
                   (@r2, @t, @u, 'Secret Society', 'Hidden Org', date '2026-06-01', date '2026-07-30', 3, 4, 'attendance', true);
            select 1;
            """,
            ("u", user), ("t", db.Data.TenantId), ("e", email), ("v", visibility), ("s", slug), ("r1", UuidV7.NewId()), ("r2", UuidV7.NewId()));
        return (user, slug);
    }

    /// <summary>A badge awarded to <paramref name="holder"/>, revoked with a reason when given one.</summary>
    private async Task<string> BadgeAsync(Guid holder, string name, string? revokedFor = null, bool expired = false)
    {
        Guid issuer = UuidV7.NewId();
        Guid badgeClass = UuidV7.NewId();
        string slug = "check-" + Guid.NewGuid().ToString("N");
        await db.ScalarAsync<int>(
            """
            insert into learning.issuer_profile (id, tenant_id, did, name) values (@i, @t, 'did:web:kettle.test:issuers:' || @i::text, 'Kettle Rowing Club');
            insert into learning.badge_class (id, tenant_id, issuer_id, slug, name, description, criteria_md, status)
            values (@c, @t, @i, @cs, @n, 'Surveyed a stretch of the river.', 'Walked and recorded two miles of riverbank.', 'published');
            insert into learning.badge_assertion (id, tenant_id, badge_class_id, recipient_user_id, recipient_identity_hash, recipient_salt,
                                                 issued_on, expires_on, credential_jwt, credential_json, verify_slug, revoked_at, revocation_reason)
            values (@a, @t, @c, @u, '\x00', '\x00', timestamptz '2026-03-01 15:00Z',
                    case when @expired then timestamptz '2026-04-01 15:00Z' else null end, 'eyJ.test', '{"type":["VerifiableCredential"]}', @slug,
                    case when @reason = '' then null else timestamptz '2026-05-01 15:00Z' end, nullif(@reason, ''));
            select 1;
            """,
            ("i", issuer), ("t", db.Data.TenantId), ("c", badgeClass), ("cs", "surveyor-" + badgeClass.ToString("N")[^8..]), ("n", name),
            ("a", UuidV7.NewId()), ("u", holder), ("expired", expired), ("slug", slug), ("reason", revokedFor ?? string.Empty));
        return slug;
    }

    [Fact]
    public async Task A_link_only_portfolio_shows_what_its_owner_chose_and_is_kept_from_search_engines()
    {
        RequireDatabase();
        (Guid user, string slug) = await PortfolioAsync("link_only");

        using Browser browser = db.NewBrowser();
        HttpResponseMessage page = await browser.GetAsync($"/portfolios/{slug}");

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(browser.Html);
        html.ShouldContain("<h1 id=\"portfolio-title\" dir=\"auto\">Kofi R</h1>");
        html.ShouldContain("Rower and chess player");
        html.ShouldContain("I row on the river.");
        html.ShouldContain("Junior Rowing");
        html.ShouldContain("Kettle Rowing Club");
        html.ShouldContain("Sessions: 5 · Hours: 12.5");
        html.ShouldNotContain("Secret Society", Case.Sensitive, "a hidden record is never shown");
        html.ShouldNotContain("Hidden Org");
        html.ShouldNotContain(user.ToString(), Case.Sensitive, "nothing of the account");
        html.ShouldContain("<meta name=\"robots\" content=\"noindex, nofollow\"");
        page.Headers.GetValues("X-Robots-Tag").ShouldContain("noindex, nofollow");
        page.Headers.CacheControl!.NoStore.ShouldBeTrue();
        html.ShouldNotContain("portfolio.page.");

        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.portfolio.public_read' and subject_ids @> array[@u]", ("u", user)))
            .ShouldBeGreaterThan(0, "a public read is audited like any other");
    }

    [Fact]
    public async Task A_public_portfolio_may_be_indexed()
    {
        RequireDatabase();
        (_, string slug) = await PortfolioAsync("public");

        using Browser browser = db.NewBrowser();
        HttpResponseMessage page = await browser.GetAsync($"/portfolios/{slug}");

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldNotContain("noindex");
        page.Headers.Contains("X-Robots-Tag").ShouldBeFalse();
    }

    [Fact]
    public async Task A_private_portfolio_and_a_name_never_used_are_both_not_found()
    {
        RequireDatabase();
        (_, string slug) = await PortfolioAsync("private");

        using Browser browser = db.NewBrowser();
        (await browser.GetAsync($"/portfolios/{slug}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        browser.Html.ShouldNotContain("Kofi R");
        (await browser.GetAsync("/portfolios/nobody-has-this-name")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await browser.GetAsync("/portfolios/NOT_A_SLUG")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_badge_check_says_whether_it_stands_and_never_who_holds_it()
    {
        RequireDatabase();
        (Guid holder, _) = await PortfolioAsync("private");
        string valid = await BadgeAsync(holder, "River Surveyor");
        string revoked = await BadgeAsync(holder, "First Aid", revokedFor: "Awarded to the wrong person.");
        string expired = await BadgeAsync(holder, "Lifeguard", expired: true);

        using Browser browser = db.NewBrowser();
        HttpResponseMessage page = await browser.GetAsync($"/badges/{valid}");
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(browser.Html);
        html.ShouldContain("River Surveyor");
        html.ShouldContain("This badge is genuine and still stands.");
        html.ShouldContain("Kettle Rowing Club");
        html.ShouldContain($"href=\"/api/v1/public/badges/{valid}\"", Case.Sensitive, "software checks the credential itself");
        html.ShouldNotContain("Kofi", Case.Sensitive, "a badge check never names its holder");
        html.ShouldContain("<meta name=\"robots\" content=\"noindex, nofollow\"");
        page.Headers.CacheControl!.NoStore.ShouldBeTrue("a revocation shows at once");
        html.ShouldNotContain("badge.page.");

        await browser.GetAsync($"/badges/{revoked}");
        html = WebUtility.HtmlDecode(browser.Html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);
        html.ShouldContain("Kettle Rowing Club took this badge back on");
        html.ShouldContain("Awarded to the wrong person.");

        await browser.GetAsync($"/badges/{expired}");
        WebUtility.HtmlDecode(browser.Html).ShouldContain("This badge is genuine, but it expired on");

        (await browser.GetAsync("/badges/no-such-badge")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
