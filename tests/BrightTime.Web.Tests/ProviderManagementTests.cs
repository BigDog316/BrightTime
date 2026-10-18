using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Provider;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Provider management through the real pipeline: applying, invitations, staff, sites, the
/// profile, seasonal cloning, scheduled publication and bulk import. Each test works on an
/// organisation no other test touches (see the fixture's rule).
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed partial class ProviderManagementTests(ConsoleFixture db)
{
    private SeedOrganisation Org(string slug) => db.Data.Organisations.Single(o => o.Slug == slug);

    private static string Admin(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private static string Staff(SeedOrganisation org) => $"staff@{org.Slug}.example.org";

    private static string NewAddress(string who) => $"{who}-{Guid.NewGuid():N}@example.org";

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private Task<Guid> NeighbourhoodAsync() =>
        db.ScalarAsync<Guid>("select id from platform.neighbourhood where tenant_id = @t and kind = 'neighbourhood' and boundary is not null and centroid is not null order by code limit 1",
            ("t", KettleSeed.TenantId));

    private async Task<(string, string)[]> ApplicationAsync(string name, string? email)
    {
        Guid hood = await NeighbourhoodAsync();
        List<(string, string)> fields =
        [
            ("DisplayName", name), ("LegalName", name + " Inc."), ("Kind", "nonprofit"), ("Mission", "Chess for every age."),
            ("WebsiteUrl", "https://example.org/chess"), ("Languages", "en"),
            ("Site.Name", name + " Hall"), ("Site.AddressLine1", "12 Board Street"), ("Site.Locality", "Kettleton"),
            ("Site.PostalCode", "10599"), ("Site.NeighbourhoodId", hood.ToString()), ("Site.IsAddressPublic", "true"),
        ];
        if (email is not null)
        {
            fields.Add(("Email", email));
        }

        return [.. fields];
    }

    // ---- applying ----------------------------------------------------------------------------

    [Fact]
    public async Task A_newcomer_applies_and_owns_the_organisation_once_they_set_a_password()
    {
        RequireDatabase();
        string email = NewAddress("chess");
        string name = "Chess Corner " + Guid.NewGuid().ToString("N")[..6];
        using Browser browser = db.NewBrowser();

        await browser.GetAsync("/providers/apply");
        HttpResponseMessage response = await browser.PostAsync("/providers/apply", "apply", await ApplicationAsync(name, email));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        Location(response).ShouldBe("/providers/apply?sent=1");

        Guid org = await db.ScalarAsync<Guid>("select id from directory.organisation where display_name = @n", ("n", name));
        (await db.ScalarAsync<string>("select status from directory.organisation where id = @o", ("o", org))).ShouldBe("applied");
        (await db.ScalarAsync<string>(
            "select m.status || ':' || m.org_role from directory.org_membership m join identity.app_user u on u.id = m.user_id where m.org_id = @o and u.email = @e",
            ("o", org), ("e", email))).ShouldBe("active:owner");
        (await db.ScalarAsync<bool>("select password_hash is null from identity.app_user where email = @e", ("e", email))).ShouldBeTrue();
        (await db.ScalarAsync<long>(
            "select count(*) from identity.app_user_role ur join identity.app_role r on r.id = ur.role_id join identity.app_user u on u.id = ur.user_id where u.email = @e and r.name = 'provider_admin'",
            ("e", email))).ShouldBe(1);
        (await db.ScalarAsync<string>("select geocode_accuracy from directory.site where org_id = @o", ("o", org))).ShouldBe("locality");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'organisation.applied' and @o = any(subject_ids)", ("o", org))).ShouldBe(1);

        EmailMessage mail = (await db.Mail.WaitForAsync(email)).ShouldNotBeNull();
        mail.TextBody.ShouldContain("/account/reset?token=");
        mail.Subject.ShouldContain(name);
    }

    [Fact]
    public async Task An_application_in_an_existing_name_reads_the_same_and_waits_for_its_owner()
    {
        RequireDatabase();
        SeedOrganisation existing = Org("alder-crossing-robotics-boosters");
        string email = Admin(existing);
        string name = "Robot Garage " + Guid.NewGuid().ToString("N")[..6];
        using Browser stranger = db.NewBrowser();

        await stranger.GetAsync("/providers/apply");
        HttpResponseMessage response = await stranger.PostAsync("/providers/apply", "apply", await ApplicationAsync(name, email));

        // The same answer as for a new address (D23): nothing says the account exists.
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Location(response).ShouldBe("/providers/apply?sent=1");
        Guid org = await db.ScalarAsync<Guid>("select id from directory.organisation where display_name = @n", ("n", name));
        Guid membership = await db.ScalarAsync<Guid>(
            "select id from directory.org_membership where org_id = @o and status = 'invited' and org_role = 'owner'", ("o", org));

        EmailMessage mail = (await db.Mail.WaitForAsync(email)).ShouldNotBeNull();
        mail.TextBody.ShouldContain("/sign-in?returnUrl=%2Fprovider");
        mail.TextBody.ShouldNotContain("/account/reset");

        // The real owner signs in and accepts; the organisation opens at once.
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(email);
        await owner.GetAsync("/provider");
        owner.Html.ShouldContain(name);
        HttpResponseMessage accepted = await owner.PostAsync("/provider", "answer-" + membership.ToString("N"),
            ("membership", membership.ToString()), ("answer", "accept"));

        Location(accepted).ShouldBe("/provider?answered=accepted");
        (await db.ScalarAsync<string>("select status from directory.org_membership where id = @m", ("m", membership))).ShouldBe("active");
        (await owner.GetAsync($"/provider/orgs/{org}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_youth_address_gets_the_same_answer_and_no_organisation()
    {
        RequireDatabase();
        string email = NewAddress("teen");
        await db.ScalarAsync<int>(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email,
                                           security_stamp, concurrency_stamp, account_kind, age_band)
            values (@id, @t, @e, upper(@e), @e, upper(@e), 'stamp', 'concurrency', 'youth', '15_17')
            returning 1
            """, ("id", UuidV7.NewId()), ("t", KettleSeed.TenantId), ("e", email));
        string name = "Teen Startup " + Guid.NewGuid().ToString("N")[..6];
        using Browser browser = db.NewBrowser();

        await browser.GetAsync("/providers/apply");
        HttpResponseMessage response = await browser.PostAsync("/providers/apply", "apply", await ApplicationAsync(name, email));

        Location(response).ShouldBe("/providers/apply?sent=1");
        (await db.ScalarAsync<long>("select count(*) from directory.organisation where display_name = @n", ("n", name))).ShouldBe(0);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        db.Mail.To(email).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_application_with_problems_says_which_fields()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();
        await browser.GetAsync("/providers/apply");

        HttpResponseMessage response = await browser.PostAsync("/providers/apply", "apply",
            ("Email", "not-an-address"), ("DisplayName", ""), ("Kind", "nonprofit"), ("Site.Name", "Somewhere"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("error-summary");
        browser.Html.ShouldContain("href=\"#Email\"");
        browser.Html.ShouldContain("href=\"#DisplayName\"");
        browser.Html.ShouldContain("href=\"#Site.NeighbourhoodId\"");
    }

    [Fact]
    public async Task A_signed_in_adult_applies_and_runs_the_new_organisation_straight_away()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();
        await browser.SignInAsync(Admin(Org("arab-american-family-center")));
        string name = "Family Chess " + Guid.NewGuid().ToString("N")[..6];

        await browser.GetAsync("/providers/apply");
        browser.Html.ShouldNotContain("id=\"Email\"");
        HttpResponseMessage response = await browser.PostAsync("/providers/apply", "apply", await ApplicationAsync(name, null));

        Guid org = await db.ScalarAsync<Guid>("select id from directory.organisation where display_name = @n", ("n", name));
        Location(response).ShouldBe($"/provider/orgs/{org}?applied=1");
        (await browser.GetAsync($"/provider/orgs/{org}?applied=1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Html.ShouldContain("A network organiser will review it.");
    }

    // ---- staff -------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_invites_a_newcomer_who_gets_a_set_password_link()
    {
        RequireDatabase();
        SeedOrganisation org = Org("casa-de-la-comunidad-kettleton");
        string email = NewAddress("helper");
        Guid site = db.Data.Sites.First(s => s.OrgId == org.Id).Id;
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        string path = $"/provider/orgs/{org.Id}/staff";
        await owner.GetAsync(path);
        HttpResponseMessage response = await owner.PostAsync(path, "invite",
            ("Email", email), ("Role", "staff"), ("Title", "Homework helper"), ("Sites", site.ToString()));

        Location(response).ShouldBe($"{path}?done=invited");
        (await db.ScalarAsync<string>(
            "select m.status || ':' || m.org_role || ':' || m.title from directory.org_membership m join identity.app_user u on u.id = m.user_id where m.org_id = @o and u.email = @e",
            ("o", org.Id), ("e", email))).ShouldBe("invited:staff:Homework helper");
        (await db.ScalarAsync<long>(
            "select count(*) from directory.site_assignment a join directory.org_membership m on m.id = a.org_membership_id join identity.app_user u on u.id = m.user_id where u.email = @e and a.site_id = @s",
            ("e", email), ("s", site))).ShouldBe(1);

        EmailMessage mail = (await db.Mail.WaitForAsync(email)).ShouldNotBeNull();
        mail.TextBody.ShouldContain("/account/reset?token=");

        await owner.GetAsync(path);
        owner.Html.ShouldContain(email);
    }

    [Fact]
    public async Task Staff_see_the_staff_list_but_cannot_invite()
    {
        RequireDatabase();
        SeedOrganisation org = Org("casa-de-la-comunidad-kettleton");
        string path = $"/provider/orgs/{org.Id}/staff";
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(Staff(org));

        (await staff.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldNotContain("invite-heading");
        staff.Html.ShouldNotContain("<summary>Manage</summary>");

        long before = await db.ScalarAsync<long>("select count(*) from directory.org_membership where org_id = @o", ("o", org.Id));
        await staff.PostAsync(path, "invite", ("Email", NewAddress("sneaky")), ("Role", "owner"));
        (await db.ScalarAsync<long>("select count(*) from directory.org_membership where org_id = @o", ("o", org.Id))).ShouldBe(before);
    }

    [Fact]
    public async Task Making_staff_an_admin_grants_the_administrator_role()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-youth-conservation-corps");
        string path = $"/provider/orgs/{org.Id}/staff";
        Guid membership = await db.ScalarAsync<Guid>(
            "select m.id from directory.org_membership m where m.org_id = @o and m.org_role = 'staff' limit 1", ("o", org.Id));
        Guid user = await db.ScalarAsync<Guid>("select user_id from directory.org_membership where id = @m", ("m", membership));
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        await owner.GetAsync(path);
        HttpResponseMessage response = await owner.PostAsync(path, "role-" + membership.ToString("N"), ("membership", membership.ToString()), ("role", "admin"));

        Location(response).ShouldBe($"{path}?done=role");
        (await db.ScalarAsync<string>("select org_role from directory.org_membership where id = @m", ("m", membership))).ShouldBe("admin");
        (await db.ScalarAsync<long>(
            "select count(*) from identity.app_user_role ur join identity.app_role r on r.id = ur.role_id where ur.user_id = @u and r.name = 'provider_admin'",
            ("u", user))).ShouldBe(1);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'staff.role_changed' and @u = any(subject_ids)", ("u", user)))
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_last_owner_can_be_neither_demoted_nor_removed()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-youth-orchestra");
        Guid ownerMembership = await db.ScalarAsync<Guid>(
            "select id from directory.org_membership where org_id = @o and org_role = 'owner'", ("o", org.Id));

        await using AsyncServiceScope scope = TenantScope();
        StaffDirectory staff = scope.ServiceProvider.GetRequiredService<StaffDirectory>();
        UserId actor = new(SeedIds.For("user/" + Admin(org)));

        (await staff.ChangeRoleAsync(org.Id, ownerMembership, "admin", actor, "owner", TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("staff.last_owner");
        (await staff.RemoveAsync(org.Id, ownerMembership, actor, "owner", TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("staff.last_owner");
        (await staff.ChangeRoleAsync(org.Id, ownerMembership, "staff", actor, "admin", TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("staff.owner_only");
    }

    // ---- sites and profile ---------------------------------------------------------------------

    [Fact]
    public async Task Staff_add_a_site_at_its_neighbourhood_with_hours_and_access()
    {
        RequireDatabase();
        SeedOrganisation org = Org("fenwick-hollow-photography-club");
        Guid hood = await NeighbourhoodAsync();
        string name = "Darkroom " + Guid.NewGuid().ToString("N")[..6];
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        string path = $"/provider/orgs/{org.Id}/sites/new";
        await owner.GetAsync(path);
        HttpResponseMessage response = await owner.PostAsync(path, "site",
            ("Name", name), ("AddressLine1", "4 Lens Lane"), ("Locality", "Fenwick Hollow"), ("PostalCode", "10598"),
            ("NeighbourhoodId", hood.ToString()), ("IsAddressPublic", "true"), ("Wheelchair", "yes"), ("Elevator", "no"),
            ("Hours[0].Day", "2"), ("Hours[0].Opens", "15:00"), ("Hours[0].Closes", "18:00"), ("Hours[0].Season", "school_year"),
            ("Hours[1].Day", "3"), ("Hours[1].Opens", ""), ("Hours[1].Closes", ""), ("Hours[1].Season", "all_year"));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, owner.Html);
        Guid site = await db.ScalarAsync<Guid>("select id from directory.site where org_id = @o and name = @n", ("o", org.Id), ("n", name));
        Location(response).ShouldBe($"/provider/orgs/{org.Id}/sites/{site}?saved=1");
        (await db.ScalarAsync<string>(
            "select geocode_accuracy || ':' || is_wheelchair_accessible || ':' || has_elevator || ':' || (has_quiet_space is null) from directory.site where id = @s",
            ("s", site))).ShouldBe("locality:true:false:true");
        (await db.ScalarAsync<long>("select count(*) from directory.site_hours where site_id = @s", ("s", site))).ShouldBe(1);
        (await db.ScalarAsync<bool>(
            "select st_dwithin(s.location, n.centroid, 1) from directory.site s join platform.neighbourhood n on n.id = s.neighbourhood_id where s.id = @s",
            ("s", site))).ShouldBeTrue();
    }

    [Fact]
    public async Task Editing_or_closing_a_site_queues_its_listings_for_reprojection()
    {
        RequireDatabase();
        SeedOrganisation org = Org("wexley-learning-studio");
        Guid site = db.Data.Sites.First(s => s.OrgId == org.Id).Id;
        string path = $"/provider/orgs/{org.Id}/sites/{site}";
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        await owner.GetAsync(path);
        HttpResponseMessage closed = await owner.PostAsync(path, "open-state");

        Location(closed).ShouldBe(path);
        (await db.ScalarAsync<bool>("select is_active from directory.site where id = @s", ("s", site))).ShouldBeFalse();
        (await db.ScalarAsync<long>(
            "select count(*) from platform.outbox_message where message_type = 'directory.site.changed' and payload->>'siteId' = @s and payload->>'isActive' = 'false'",
            ("s", site.ToString()))).ShouldBe(1);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'site.deactivated' and @s = any(subject_ids)", ("s", site))).ShouldBe(1);

        await owner.GetAsync(path);
        owner.Html.ShouldContain("This site is closed.");
    }

    [Fact]
    public async Task An_owner_edits_the_profile_and_a_stale_form_is_refused()
    {
        RequireDatabase();
        SeedOrganisation org = Org("lakou-ayisyen-youth-program");
        string path = $"/provider/orgs/{org.Id}/profile";
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        await owner.GetAsync(path);
        string version = VersionPattern().Match(owner.Html).Groups[1].Value;
        (string, string)[] fields =
        [
            ("Version", version), ("DisplayName", org.DisplayName), ("LegalName", org.LegalName), ("Kind", org.Kind),
            ("Mission", "Kreyòl and music after school."), ("WebsiteUrl", org.WebsiteUrl), ("PublicEmail", org.PublicEmail),
            ("PublicPhone", org.PublicPhone), ("Languages", "ht"), ("Languages", "en"),
        ];

        HttpResponseMessage saved = await owner.PostAsync(path, "profile", fields);
        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect, Problem(owner.Html));
        Location(saved).ShouldBe($"{path}?saved=1");
        (await db.ScalarAsync<string>("select mission from directory.organisation where id = @o", ("o", org.Id))).ShouldBe("Kreyòl and music after school.");

        // The same version again: someone saved since this form was opened.
        await owner.GetAsync(path);
        HttpResponseMessage stale = await owner.PostAsync(path, "profile", fields);
        stale.StatusCode.ShouldBe(HttpStatusCode.OK);
        owner.Html.ShouldContain("notice error");
    }

    // ---- listings: scheduling, cloning, importing ------------------------------------------------

    [Fact]
    public async Task The_editor_saves_every_other_week_days_off_and_a_show_from_day()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettleton-youth-basketball-association");
        Guid site = db.Data.Sites.First(s => s.OrgId == org.Id).Id;
        DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var form = new ListingForm
        {
            Title = "Fortnightly free throws",
            Summary = "Shooting practice every other week.",
            TermIds = [KettleTaxonomy.TermId("category", "sports")],
            SiteIds = [site],
            ExternalUrl = "https://example.org/kettle/throws",
            Days = [start.DayOfWeek.ToString()[..2].ToUpperInvariant()],
            Interval = "2",
            StartsOn = Day(start),
            EndsOn = Day(start.AddDays(70)),
            SkipDates = Day(start.AddDays(28)),
            ShowFrom = Day(start.AddDays(-7)),
            StartTime = "17:00",
            EndTime = "18:00",
        };
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        string path = $"/provider/orgs/{org.Id}/listings/new";
        await owner.GetAsync(path);
        HttpResponseMessage response = await owner.PostAsync(path, "listing", ConsoleTests.Fields(form, "save"));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, owner.Html);
        Guid id = await db.ScalarAsync<Guid>("select id from opportunity.listing where org_id = @o and title = @t", ("o", org.Id), ("t", form.Title));
        (await db.ScalarAsync<string>("select rrule from opportunity.listing_session where listing_id = @l", ("l", id))).ShouldStartWith("FREQ=WEEKLY;INTERVAL=2;BYDAY=");
        (await db.ScalarAsync<string>("select array_to_string(exdates, ',') from opportunity.listing_session where listing_id = @l", ("l", id))).ShouldBe(Day(start.AddDays(28)));
        (await db.ScalarAsync<bool>(
            "select publish_at = (@d::date)::timestamp at time zone 'America/New_York' from opportunity.listing where id = @l",
            ("l", id), ("d", Day(start.AddDays(-7))))).ShouldBeTrue();

        // And the editor shows them again.
        await owner.GetAsync($"/provider/orgs/{org.Id}/listings/{id}");
        owner.Html.ShouldContain($"value=\"{Day(start.AddDays(-7))}\"");
        owner.Html.ShouldContain(Day(start.AddDays(28)) + "</textarea>");
    }

    [Fact]
    public async Task A_listing_cloned_for_next_season_is_a_draft_with_its_schedule_moved()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettleton-youth-basketball-association");
        Guid original = await db.ScalarAsync<Guid>(
            "select l.id from opportunity.listing l where l.org_id = @o and exists (select 1 from opportunity.listing_session s where s.listing_id = l.id) order by l.slug limit 1",
            ("o", org.Id));
        DateOnly startsOn = DateOnly.Parse(await db.ScalarAsync<string>("select min(starts_on)::text from opportunity.listing_session where listing_id = @l", ("l", original)), CultureInfo.InvariantCulture);
        DateOnly newStart = startsOn.AddDays(364); // 52 weeks: the same weekday.
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        string path = $"/provider/orgs/{org.Id}/listings/{original}/clone";
        await owner.GetAsync(path);
        HttpResponseMessage response = await owner.PostAsync(path, "clone",
            ("StartsOn", newStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("SeasonLabel", "Next season"));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, owner.Html);
        Guid copy = await db.ScalarAsync<Guid>("select id from opportunity.listing where cloned_from_id = @l", ("l", original));
        (await db.ScalarAsync<string>("select status || ':' || season_label from opportunity.listing where id = @c", ("c", copy))).ShouldBe("draft:Next season");
        (await db.ScalarAsync<string>("select min(starts_on)::text from opportunity.listing_session where listing_id = @c", ("c", copy)))
            .ShouldBe(newStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        (await db.ScalarAsync<long>("select count(*) from opportunity.listing_term where listing_id = @c", ("c", copy)))
            .ShouldBe(await db.ScalarAsync<long>("select count(*) from opportunity.listing_term where listing_id = @l", ("l", original)));
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'listing.cloned' and @c = any(subject_ids)", ("c", copy))).ShouldBe(1);
    }

    [Fact]
    public async Task A_csv_import_reports_row_by_row_then_creates_drafts_and_a_second_run_updates_them()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-community-college-youth-programs");
        string site = db.Data.Sites.First(s => s.OrgId == org.Id && s.Name.Contains(',', StringComparison.Ordinal)).Name;
        string from = DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string to = DateTime.UtcNow.AddDays(90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string external = "cc-" + Guid.NewGuid().ToString("N")[..8];
        string csv =
            $"""
             id,title,summary,categories,sites,days,starts_on,ends_on,start_time,end_time,registration,registration_url
             {external},Coding camp,Learn to code games.,stem,"{site}",MO;WE,{from},{to},15:00,16:30,external,https://example.org/cc
             {external}-b,Nowhere club,Meets nowhere.,stem,No Such Place,MO,{from},{to},15:00,16:30,external,https://example.org/cc
             {external},Copy,Same id again.,stem,"{site}",MO,{from},{to},15:00,16:30,external,https://example.org/cc

             """;
        string path = $"/provider/orgs/{org.Id}/import";
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));

        await owner.GetAsync(path);
        HttpResponseMessage checkedFile = await owner.PostFileAsync(path, "upload", ("File", "listings.csv", csv), ("Format", "csv"), ("DefaultCategory", ""));

        checkedFile.StatusCode.ShouldBe(HttpStatusCode.OK);
        owner.Html.ShouldContain("New: 1. Updated: 0. Left alone: 0. With errors: 2.");
        owner.Html.ShouldContain("No open site is named");
        owner.Html.ShouldContain("is used by an earlier row");
        (await db.ScalarAsync<long>("select count(*) from opportunity.listing where source_record_id = @x", ("x", external))).ShouldBe(0, "a dry run writes nothing");

        HttpResponseMessage applied = await owner.PostAsync(path, "apply", ("payload", Payload(owner.Html)));
        applied.StatusCode.ShouldBe(HttpStatusCode.Redirect, Problem(owner.Html));
        Location(applied).ShouldBe($"{path}?created=1&updated=0&skipped=0");
        Guid listing = await db.ScalarAsync<Guid>(
            "select id from opportunity.listing where org_id = @o and source = 'csv_import' and source_record_id = @x", ("o", org.Id), ("x", external));
        (await db.ScalarAsync<string>("select status from opportunity.listing where id = @l", ("l", listing))).ShouldBe("draft");
        (await db.ScalarAsync<string>("select rrule from opportunity.listing_session where listing_id = @l", ("l", listing))).ShouldBe("FREQ=WEEKLY;BYDAY=MO,WE");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'listing.import_applied' and actor_org_id = @o", ("o", org.Id)))
            .ShouldBeGreaterThan(0);

        // The same file again updates the draft rather than adding a second one.
        await owner.GetAsync(path);
        await owner.PostFileAsync(path, "upload", ("File", "listings.csv", csv.Replace("Learn to code games.", "Learn to code apps.", StringComparison.Ordinal)), ("Format", "csv"));
        owner.Html.ShouldContain("New: 0. Updated: 1.");
        HttpResponseMessage again = await owner.PostAsync(path, "apply", ("payload", Payload(owner.Html)));
        Location(again).ShouldBe($"{path}?created=0&updated=1&skipped=0");
        (await db.ScalarAsync<long>("select count(*) from opportunity.listing where source_record_id = @x", ("x", external))).ShouldBe(1);
        (await db.ScalarAsync<string>("select summary from opportunity.listing where id = @l", ("l", listing))).ShouldBe("Learn to code apps.");
    }

    [Fact]
    public async Task A_tampered_or_foreign_import_confirmation_is_refused()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-community-college-youth-programs");
        string path = $"/provider/orgs/{org.Id}/import";
        using Browser owner = db.NewBrowser();
        await owner.SignInAsync(Admin(org));
        await owner.GetAsync(path);

        HttpResponseMessage response = await owner.PostAsync(path, "apply", ("payload", "CfDJ8-not-a-real-payload"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        owner.Html.ShouldContain("This check has expired.");
    }

    [Fact]
    public async Task Only_owners_and_admins_can_import()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-community-college-youth-programs");
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(Staff(org));

        (await staff.GetAsync($"/provider/orgs/{org.Id}/import")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await staff.GetAsync($"/provider/orgs/{org.Id}");
        staff.Html.ShouldNotContain($"/provider/orgs/{org.Id}/import\"");
    }

    [Fact]
    public async Task The_csv_template_downloads()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();

        HttpResponseMessage response = await browser.GetAsync("/provider/import-template.csv");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        browser.Html.ShouldContain("id,kind,title,summary");
    }

    /// <summary>Where a redirect goes, as a path: the server answers with absolute addresses.</summary>
    private static string Location(HttpResponseMessage response)
    {
        Uri location = response.Headers.Location.ShouldNotBeNull("a redirect");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    private AsyncServiceScope TenantScope()
    {
        AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
        return scope;
    }

    /// <summary>The page's error notices and field errors, for a failure message.</summary>
    private static string Problem(string html) =>
        string.Join(" | ", ProblemPattern().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)));

    [GeneratedRegex("class=\"(?:notice error|field-error)\"[^>]*>([^<]*)<")]
    private static partial Regex ProblemPattern();

    private static string Payload(string html)
    {
        string payload = WebUtility.HtmlDecode(PayloadPattern().Match(html).Groups[1].Value);
        payload.ShouldNotBeNullOrEmpty("the dry run offers an apply form");
        return payload;
    }

    [GeneratedRegex("name=\"payload\" value=\"([^\"]+)\"")]
    private static partial Regex PayloadPattern();

    [GeneratedRegex("name=\"Version\" value=\"([0-9]+)\"")]
    private static partial Regex VersionPattern();
}
