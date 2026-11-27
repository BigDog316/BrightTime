using System.Net;
using System.Security.Cryptography;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Tests;

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

    /// <summary>
    /// A badge awarded to <paramref name="holder"/>, revoked with a reason when given one, from an
    /// issuer of its own: the organisation's when given one, else none.
    /// </summary>
    private async Task<string> BadgeAsync(Guid holder, string name, string? revokedFor = null, bool expired = false, Guid? org = null)
    {
        Guid issuer = UuidV7.NewId();
        Guid badgeClass = UuidV7.NewId();
        string slug = "check-" + Guid.NewGuid().ToString("N");
        await db.ScalarAsync<int>(
            """
            insert into learning.issuer_profile (id, tenant_id, org_id, did, name) values (@i, @t, @o, 'did:web:kettle.test:issuers:' || @i::text, 'Kettle Rowing Club');
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
            ("a", UuidV7.NewId()), ("u", holder), ("expired", expired), ("slug", slug), ("reason", revokedFor ?? string.Empty), ("o", org is { } o ? o : DBNull.Value));
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

    /// <summary>A project with one picture, written straight to the tables, as staff approved and its maker showed it, or as a draft.</summary>
    private async Task<(Guid Project, Guid Media, byte[] Bytes)> PictureAsync(Guid user, string title, bool published)
    {
        Guid project = UuidV7.NewId();
        Guid media = UuidV7.NewId();
        byte[] bytes = TestImages.Png(640, 480);
        string key = "images/" + media.ToString("N") + ".png";
        await db.ScalarAsync<int>(
            """
            insert into learning.project (id, tenant_id, user_id, title, visibility, status, approved_at)
            values (@p, @t, @u, @title, case when @shown then 'public' else 'private' end, case when @shown then 'published' else 'draft' end,
                    case when @shown then now() else null end);
            insert into platform.media_object (id, tenant_id, object_key, content_type, byte_size, sha256, width, height, scan_state, uploaded_by, visibility)
            values (@m, @t, @k, 'image/png', @n, @h, 640, 480, 'clean', @u, 'private');
            insert into platform.media_blob (tenant_id, object_key, content_type, bytes, sha256) values (@t, @k, 'image/png', @b, @h);
            insert into learning.project_media (project_id, media_id, alt_text, caption) values (@p, @m, 'A clay boat on a table', 'Before the glaze');
            select 1;
            """,
            ("p", project), ("t", db.Data.TenantId), ("u", user), ("title", title), ("shown", published), ("m", media), ("k", key),
            ("n", (long)bytes.Length), ("h", SHA256.HashData(bytes)), ("b", bytes));
        return (project, media, bytes);
    }

    [Fact]
    public async Task A_published_projects_pictures_show_on_its_portfolio_and_nowhere_else()
    {
        RequireDatabase();
        (Guid user, string slug) = await PortfolioAsync("link_only");
        (Guid shown, Guid media, byte[] bytes) = await PictureAsync(user, "A clay boat", published: true);
        (Guid draft, Guid unseen, _) = await PictureAsync(user, "Half a bowl", published: false);
        string path = $"/portfolios/{slug}/projects/{shown}/pictures/{media}";

        using Browser browser = db.NewBrowser();
        (await browser.GetAsync($"/portfolios/{slug}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(browser.Html);
        html.ShouldContain($"<img src=\"{path}\" alt=\"A clay boat on a table\"");
        html.ShouldContain("Before the glaze");
        html.ShouldNotContain("Half a bowl");
        html.ShouldNotContain(unseen.ToString());

        HttpResponseMessage picture = await browser.GetAsync(path);
        picture.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await picture.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(bytes);
        (picture.Content.Headers.ContentType!.MediaType, picture.Headers.CacheControl!.NoStore).ShouldBe(("image/png", true));
        picture.Headers.GetValues("X-Robots-Tag").ShouldContain("noindex, nofollow");
        picture.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");

        // A draft's picture, the right picture under another portfolio or project, and the media address: all nothing.
        (await browser.GetAsync($"/portfolios/{slug}/projects/{draft}/pictures/{unseen}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await browser.GetAsync($"/portfolios/{slug}/projects/{draft}/pictures/{media}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await browser.GetAsync($"/portfolios/nobody-has-this-name/projects/{shown}/pictures/{media}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await browser.GetAsync($"/media/{media}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a young person's picture is never public media");

        // Taken back, the portfolio's pictures go with it.
        await db.ScalarAsync<int>("update learning.learner_profile set visibility = 'private' where user_id = @u; select 1;", ("u", user));
        (await browser.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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

    /// <summary>
    /// The badge's picture, described, and the issuer's logo beside its name, as decoration (D111);
    /// both greyed once the badge no longer stands. Gives Hollis Bend Community Garden a logo straight
    /// in the tables, and takes it away again.
    /// </summary>
    [Fact]
    public async Task A_badge_check_shows_the_badges_picture_and_the_issuers_logo_greyed_once_it_no_longer_stands()
    {
        RequireDatabase();
        Guid org = db.Data.Organisations.Single(o => o.Slug == "hollis-bend-community-garden").Id;
        (Guid holder, _) = await PortfolioAsync("private");
        string plain = await BadgeAsync(holder, "Seed Saver");
        string valid = await BadgeAsync(holder, "Tree Warden", org: org);
        string revoked = await BadgeAsync(holder, "Pond Keeper", revokedFor: "Awarded twice.", org: org);
        Guid logo = UuidV7.NewId();
        byte[] bytes = TestImages.Png(300, 300);
        string key = "images/" + logo.ToString("N") + ".png";
        await db.ScalarAsync<int>(
            """
            insert into platform.media_object (id, tenant_id, object_key, content_type, byte_size, sha256, width, height, scan_state, uploaded_by, visibility)
            values (@m, @t, @k, 'image/png', @n, @h, 300, 300, 'clean', (select id from identity.app_user order by id limit 1), 'public');
            insert into platform.media_blob (tenant_id, object_key, content_type, bytes, sha256) values (@t, @k, 'image/png', @b, @h);
            update directory.organisation set logo_media_id = @m where id = @o;
            select 1;
            """,
            ("m", logo), ("t", db.Data.TenantId), ("k", key), ("n", (long)bytes.Length), ("h", SHA256.HashData(bytes)), ("b", bytes), ("o", org));
        try
        {
            using Browser browser = db.NewBrowser();

            // Its picture, its own or the generated one, under the API's address on this host, described; the logo isn't read out.
            await browser.GetAsync($"/badges/{valid}");
            Guid badgeClass = await db.ScalarAsync<Guid>("select badge_class_id from learning.badge_assertion where verify_slug = @s", ("s", valid));
            browser.Html.ShouldContain("<article class=\"badge-check valid\"");
            browser.Html.ShouldContain($"<img class=\"badge-picture\" src=\"/api/v1/public/badges/classes/{badgeClass:N}/image\" alt=\"The Tree Warden badge\" width=\"96\" height=\"96\" />");
            browser.Html.ShouldContain($"<img class=\"org-logo org-logo-small\" src=\"/media/{logo:D}/480\" alt />", Case.Sensitive, "an empty alt: Blazor writes it bare");

            // Revoked, the same pictures, greyed by the page's state.
            await browser.GetAsync($"/badges/{revoked}");
            browser.Html.ShouldContain("<article class=\"badge-check revoked\"");
            browser.Html.ShouldContain("alt=\"The Pond Keeper badge\"");
            browser.Html.ShouldContain($"src=\"/media/{logo:D}/480\"");

            // An issuer with no logo, or no organisation, shows only its name.
            await browser.GetAsync($"/badges/{plain}");
            browser.Html.ShouldContain("alt=\"The Seed Saver badge\"");
            browser.Html.ShouldNotContain("org-logo");
            await db.ScalarAsync<int>("update directory.organisation set logo_media_id = null where id = @o; select 1;", ("o", org));
            await browser.GetAsync($"/badges/{valid}");
            browser.Html.ShouldNotContain("org-logo");
        }
        finally
        {
            await db.ScalarAsync<int>(
                """
                update directory.organisation set logo_media_id = null where id = @o;
                delete from platform.media_blob where tenant_id = @t and object_key = @k;
                delete from platform.media_object where id = @m;
                select 1;
                """,
                ("o", org), ("t", db.Data.TenantId), ("k", key), ("m", logo));
        }
    }
}
