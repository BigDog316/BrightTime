using System.Globalization;
using System.Net;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Web.Tests;

/// <summary>
/// The county's clearance rules (D99): off, nobody is refused; an organiser sees how many would
/// be and turns them on with an extra kind (audited); then a staff member and the owner without
/// clearances are refused the young people's pages but keep the rest, are told why, and the
/// refusal is audited; the owner can't record their own clearances, records the staff member's,
/// and the staff member is let back in. Turned off again at the end, for every other test.
/// </summary>
/// <remarks>
/// Uses the teen health collaborative, whose only other use is the external sign-in test's owner.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ClearanceRulesTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private const string Rules = "/organiser/settings/clearances";

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Turned_on_the_rules_close_young_peoples_pages_to_anyone_uncleared_until_someone_else_records_them()
    {
        RequireDatabase();
        SeedOrganisation org = db.Data.Organisations.Single(o => o.Slug == "kettle-teen-health-collaborative");
        string ownerEmail = $"admin@{org.Slug}.example.org";
        string staffEmail = $"staff@{org.Slug}.example.org";
        Guid ownerId = await db.ScalarAsync<Guid>("select id from identity.app_user where email = @e", ("e", ownerEmail));
        Guid staffId = await db.ScalarAsync<Guid>("select id from identity.app_user where email = @e", ("e", staffEmail));
        Guid ownerMembership = await db.ScalarAsync<Guid>("select id from directory.org_membership where org_id = @o and user_id = @u", ("o", org.Id), ("u", ownerId));
        Guid staffMembership = await db.ScalarAsync<Guid>("select id from directory.org_membership where org_id = @o and user_id = @u", ("o", org.Id), ("u", staffId));
        string attendance = $"/provider/orgs/{org.Id}/attendance";
        string listings = $"/provider/orgs/{org.Id}";
        string staffPage = $"/provider/orgs/{org.Id}/staff";
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(ownerEmail);
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(staffEmail);
        try
        {
            // Off: nobody is refused, and the organiser sees how many would be.
            (await staff.GetAsync(attendance)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await organiser.GetAsync(Rules)).StatusCode.ShouldBe(HttpStatusCode.OK);
            organiser.Html.ShouldContain("href=\"/organiser/settings/clearances\"", Case.Sensitive, "the console's nav links to it");
            string shown = Shown(organiser.Html);
            shown.ShouldContain("Clearances aren't required yet.");
            shown.ShouldContain("People at approved organisations who would be refused if you turned this on:");
            organiser.Html.ShouldNotContain("clearance_rules.");

            // On, with CPR and first aid added: audited.
            (await organiser.PostAsync(Rules, "rules", ("Enforced", "true"), ("Also", "cpr_first_aid"))).Headers.Location!.ToString().ShouldEndWith($"{Rules}?done=saved");
            (await db.ScalarAsync<long>("select count(*) from platform.feature_flag where key in ('clearances.required', 'clearances.also.cpr_first_aid') and is_enabled")).ShouldBe(2);
            (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'platform.feature_flag.changed' and detail->>'clearances.required' = 'on'")).ShouldBeGreaterThan(0);
            await organiser.GetAsync(Rules);
            Shown(organiser.Html).ShouldContain("Clearances are required.");

            // The staff member and the owner, with none on file, are refused the young people's
            // pages, keep the rest, and are told why. The refusal is audited.
            (await staff.GetAsync(attendance)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await owner.GetAsync(attendance)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "owners are no exception");
            (await staff.GetAsync(listings)).StatusCode.ShouldBe(HttpStatusCode.OK);
            shown = Shown(staff.Html);
            shown.ShouldContain("You aren't cleared to work with young people here, so their records are closed to you.");
            shown.ShouldContain("Not on file: Background check, Mandated reporter training, CPR and first aid.");
            staff.Html.ShouldNotContain("clearances.gate");
            (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'directory.clearance.refused' and actor_id = @u and outcome = 'deny'", ("u", staffId)))
                .ShouldBeGreaterThan(0);

            // The owner keeps the staff page and the clearances pages, but can't record their own.
            (await owner.GetAsync(staffPage)).StatusCode.ShouldBe(HttpStatusCode.OK);
            string ownPage = $"{staffPage}/{ownerMembership}/clearances";
            (await owner.GetAsync(ownPage)).StatusCode.ShouldBe(HttpStatusCode.OK);
            Shown(owner.Html).ShouldContain("The county requires these before anyone here acts on a young person's record.");
            (await owner.PostAsync(ownPage, "record", ("Kind", "background_check"), ("Status", "cleared"), ("CompletedOn", Day(today.AddDays(-20))), ("ExpiresOn", ""))).StatusCode
                .ShouldBe(HttpStatusCode.OK);
            Shown(owner.Html).ShouldContain("Someone else records your own clearances");
            (await db.ScalarAsync<long>("select count(*) from directory.staff_clearance where user_id = @u", ("u", ownerId))).ShouldBe(0);

            // The owner records the staff member's three, and the staff member is let back in.
            string theirs = $"{staffPage}/{staffMembership}/clearances";
            foreach (string kind in new[] { "background_check", "mandated_reporter_training", "cpr_first_aid" })
            {
                await owner.GetAsync(theirs);
                (await owner.PostAsync(theirs, "record", ("Kind", kind), ("Status", "cleared"), ("CompletedOn", Day(today.AddDays(-20))), ("ExpiresOn", Day(today.AddYears(1)))))
                    .StatusCode.ShouldBe(HttpStatusCode.Redirect, owner.Html);
            }

            (await staff.GetAsync(attendance)).StatusCode.ShouldBe(HttpStatusCode.OK);
            await staff.GetAsync(listings);
            staff.Html.ShouldNotContain("aren&#x27;t cleared to work with young people here");
            Shown(staff.Html).ShouldNotContain("You aren't cleared to work with young people here");
        }
        finally
        {
            await db.ScalarAsync<long>("with gone as (delete from platform.feature_flag where key like 'clearances.%' returning 1) select count(*) from gone");
        }

        (await staff.GetAsync(attendance)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync(attendance)).StatusCode.ShouldBe(HttpStatusCode.OK, "off again, nobody is refused");
    }
}
