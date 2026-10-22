using System.Net;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's Reports page (D58): the reports an organisation's staff may run, each a
/// form that downloads the file straight away (D57). Who may, and what a bad request gets back.
/// </summary>
/// <remarks>
/// The admins of the first organisation from sorted index 47 on that has a listing, and of the next
/// organisation after it; no other web test uses them.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ReportPageTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private ((Guid Org, string Admin) Mine, string OtherAdmin) Organisations()
    {
        var sorted = db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ToList();
        int mine = Enumerable.Range(47, sorted.Count - 47).First(i => db.Data.Listings.Any(l => l.OrgId == sorted[i].Id));
        return ((sorted[mine].Id, $"admin@{sorted[mine].Slug}.example.org"), $"admin@{sorted[mine + 1].Slug}.example.org");
    }

    private static string Day(int offset) => DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Staff_download_their_reports_from_the_console_and_nobody_else_can()
    {
        RequireDatabase();
        ((Guid org, string admin), string otherAdmin) = Organisations();
        string page = $"/provider/orgs/{org}/reports";
        string download = $"{page}/download";

        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(admin);
        (await browser.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain($"href=\"{page}\" aria-current=\"page\"");
        foreach (string key in new[] { "provider.roster", "provider.attendance", "provider.participation" })
        {
            browser.Html.ShouldContain($"name=\"reportKey\" value=\"{key}\"");
        }

        // Participation as CSV: straight back, never cached, and on record.
        HttpResponseMessage csv = await browser.PostAsync(download, null, ("reportKey", "provider.participation"), ("from", Day(-30)), ("to", Day(0)), ("format", "csv"));
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        csv.Headers.CacheControl!.NoStore.ShouldBeTrue();
        csv.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldStartWith("provider-participation-");
        browser.Html.ShouldContain("Participation");

        // A roster of one of its activities, as a spreadsheet.
        await browser.GetAsync(page);
        Guid listing = db.Data.Listings.First(l => l.OrgId == org).Id;
        HttpResponseMessage xlsx = await browser.PostAsync(download, null, ("reportKey", "provider.roster"), ("listingId", listing.ToString()), ("format", "xlsx"));
        xlsx.StatusCode.ShouldBe(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        (await db.ScalarAsync<long>("select count(*) from analytics.report_run where scope->>'orgId' = @o and media_id is null", ("o", org.ToString()))).ShouldBe(2);

        // Too long a period comes back to the page, saying so.
        await browser.GetAsync(page);
        HttpResponseMessage tooLong = await browser.PostAsync(download, null,
            ("reportKey", "provider.attendance"), ("listingId", listing.ToString()), ("from", Day(-60)), ("to", Day(0)), ("format", "pdf"));
        tooLong.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        tooLong.Headers.Location!.ToString().ShouldBe($"{page}?problem=reports.range");
        await browser.GetAsync(tooLong.Headers.Location.ToString());
        browser.Html.ShouldContain("Choose a shorter period.");

        // Another organisation's admin sees neither the page nor a file.
        using Browser other = db.NewBrowser();
        await other.SignInAsync(otherAdmin);
        (await other.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await other.GetAsync($"/provider");
        HttpResponseMessage refused = await other.PostAsync(download, null, ("reportKey", "provider.participation"), ("format", "csv"));
        refused.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        refused.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/csv");
        (await db.ScalarAsync<long>("select count(*) from analytics.report_run where scope->>'orgId' = @o", ("o", org.ToString()))).ShouldBe(2);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'reports.provider.participation' and outcome = 'deny' and actor_org_id = @o",
            ("o", org))).ShouldBeGreaterThanOrEqualTo(1);
    }
}
