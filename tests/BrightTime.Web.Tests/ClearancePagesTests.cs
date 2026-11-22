using System.Globalization;
using System.Net;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Web.Tests;

/// <summary>
/// Staff clearances in the console (D98): an organisation's owner sees who isn't cleared to work
/// with young people, records a staff member's clearances (a bad one refused, a county-wide one
/// not theirs to make), and sees what expires soon; staff and another organisation's admin are
/// turned away; an organiser reaches the same page from the organisation's review and records a
/// clearance for the whole county. Every record is audited.
/// </summary>
/// <remarks>
/// Uses the swim club, whose only other use is the passkey tests' sign-in.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ClearancePagesTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task An_owner_and_an_organiser_record_a_staff_members_clearances()
    {
        RequireDatabase();
        SeedOrganisation org = db.Data.Organisations.Single(o => o.Slug == "kettle-county-swim-club");
        SeedOrganisation other = db.Data.Organisations.Single(o => o.Slug == "casa-de-la-comunidad-kettleton");
        string staffEmail = $"staff@{org.Slug}.example.org";
        Guid staffId = await db.ScalarAsync<Guid>("select id from identity.app_user where email = @e", ("e", staffEmail));
        Guid membership = await db.ScalarAsync<Guid>("select id from directory.org_membership where org_id = @o and user_id = @u", ("o", org.Id), ("u", staffId));
        string staffPage = $"/provider/orgs/{org.Id}/staff";
        string page = $"{staffPage}/{membership}/clearances";
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        using Browser owner = db.NewBrowser();
        await owner.SignInAsync($"admin@{org.Slug}.example.org");

        // The staff page says who isn't cleared, and links to their clearances.
        (await owner.GetAsync(staffPage)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(owner.Html).ShouldContain("Not yet cleared to work with young people");
        owner.Html.ShouldContain($"href=\"{page}\"");
        owner.Html.ShouldNotContain("clearances.");

        (await owner.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string shown = Shown(owner.Html);
        shown.ShouldContain($"Clearances: {staffEmail}");
        shown.ShouldContain("Working with young people needs: Background check, Mandated reporter training.");
        shown.ShouldContain("Not on file: Background check, Mandated reporter training");
        owner.Html.ShouldNotContain("name=\"CountyWide\"", Case.Sensitive, "an organisation records only its own");
        owner.Html.ShouldNotContain("clearances.");

        // Cleared without a date it was completed: refused, nothing kept.
        (await owner.PostAsync(page, "record", ("Kind", "background_check"), ("Status", "cleared"), ("CompletedOn", ""), ("ExpiresOn", ""))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        Shown(owner.Html).ShouldContain("Give the date it was completed.");
        (await db.ScalarAsync<long>("select count(*) from directory.staff_clearance where user_id = @u", ("u", staffId))).ShouldBe(0);

        // A background check expiring in three weeks, even with the county box forged in, is the organisation's.
        await owner.GetAsync(page);
        DateOnly expires = today.AddDays(21);
        HttpResponseMessage recorded = await owner.PostAsync(page, "record", ("Kind", "background_check"), ("Status", "cleared"),
            ("CompletedOn", Day(today.AddDays(-300))), ("ExpiresOn", Day(expires)), ("CountyWide", "true"));
        recorded.Headers.Location!.ToString().ShouldEndWith($"{page}?done=recorded");
        (await db.ScalarAsync<string>("select status || ':' || coalesce(org_id::text, 'county') from directory.staff_clearance where user_id = @u and clearance_kind = 'background_check'", ("u", staffId)))
            .ShouldBe($"cleared:{org.Id}");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'directory.clearance.recorded' and @u = any(subject_ids)", ("u", staffId))).ShouldBe(1);

        await owner.GetAsync(page);
        await owner.PostAsync(page, "record", ("Kind", "mandated_reporter_training"), ("Status", "cleared"), ("CompletedOn", Day(today.AddDays(-10))), ("ExpiresOn", ""));
        await owner.GetAsync(page);
        shown = Shown(owner.Html);
        shown.ShouldContain("Cleared to work with young people");
        shown.ShouldContain("Background check expires on");
        shown.ShouldContain("No expiry");

        // Staff and another organisation's admin are turned away.
        using (Browser staff = db.NewBrowser())
        {
            await staff.SignInAsync(staffEmail);
            (await staff.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await staff.GetAsync(staffPage)).StatusCode.ShouldBe(HttpStatusCode.OK);
            staff.Html.ShouldNotContain($"href=\"{page}\"");
        }

        using (Browser intruder = db.NewBrowser())
        {
            await intruder.SignInAsync($"admin@{other.Slug}.example.org");
            (await intruder.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // An organiser reaches it from the organisation's review, and records for the whole county.
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        (await organiser.GetAsync($"/organiser/review/organisations/{org.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Staff clearances");
        organiser.Html.ShouldContain($"href=\"{page}\"");
        (await organiser.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("name=\"CountyWide\"");
        (await organiser.PostAsync(page, "record", ("Kind", "fingerprint"), ("Status", "cleared"),
            ("CompletedOn", Day(today.AddDays(-5))), ("ExpiresOn", Day(today.AddYears(2))), ("CountyWide", "true")))
            .Headers.Location!.ToString().ShouldEndWith($"{page}?done=recorded");
        (await db.ScalarAsync<long>("select count(*) from directory.staff_clearance where user_id = @u and clearance_kind = 'fingerprint' and org_id is null", ("u", staffId))).ShouldBe(1);
        await organiser.GetAsync(page);
        Shown(organiser.Html).ShouldContain("The whole county");
    }
}
