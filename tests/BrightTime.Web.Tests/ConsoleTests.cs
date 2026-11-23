using System.Globalization;
using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console and the review queue, through the real pipeline (cookies,
/// antiforgery, authorisation, redirects) against a seeded database of their own.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class ConsoleTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private SeedOrganisation Org(string slug) => db.Data.Organisations.Single(o => o.Slug == slug);

    private static string Admin(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    [Fact]
    public async Task Anonymous_requests_to_the_consoles_go_to_sign_in()
    {
        RequireDatabase();
        using Browser browser = db.NewBrowser();

        foreach (string path in new[] { "/provider", "/organiser/review" })
        {
            HttpResponseMessage response = await browser.GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect, path);
            response.Headers.Location!.ToString().ShouldContain("/sign-in?returnUrl=");
        }
    }

    [Fact]
    public async Task Signing_in_without_an_antiforgery_token_is_refused()
    {
        RequireDatabase();
        using HttpClient client = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: false));
        using var form = new FormUrlEncodedContent([new("userId", SeedIds.For("user/" + Organiser).ToString())]);

        HttpResponseMessage response = await client.PostAsync(new Uri("/dev/sign-in/as", UriKind.Relative), form, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task A_provider_drafts_and_submits_and_an_organiser_publishes()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-youth-collective");

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        Guid id = await CreateAsync(provider, org, "Kite building club", "submit");

        (await StatusAsync(id)).ShouldBe("pending_review");
        provider.Html.ShouldNotContain("Save and submit");

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync("/organiser/review");
        organiser.Html.ShouldContain("Kite building club");

        await organiser.GetAsync($"/organiser/review/listings/{id}");
        HttpResponseMessage published = await organiser.PostAsync($"/organiser/review/listings/{id}", "publish");

        published.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await StatusAsync(id)).ShouldBe("published");
        (await db.ScalarAsync<Guid>("select reviewed_by from opportunity.listing where id = @id", ("id", id)))
            .ShouldBe(KettleAccounts.OrganiserId);
        (await db.ScalarAsync<long>(
            "select count(*) from platform.job where unique_key = @k and state = 'ready'",
            ("k", $"opportunities.project_listing:{id:N}"))).ShouldBe(1, "the worker is told to project it");
        (await DecisionEmailsAsync(id, "published")).ShouldBe(await LooksAfterAsync(org.Id), "the admin who submitted it, and every owner and admin, once each (D102)");

        // Each change is on the audit trail, written in the change's own transaction.
        Guid providerId = SeedIds.For("user/" + Admin(org));
        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'listing.created' and actor_id = @a and actor_org_id = @o and subject_ids = array[@id]",
            ("a", providerId), ("o", org.Id), ("id", id))).ShouldBe(1);
        (await db.ScalarAsync<long>(
            """
            select count(*) from platform.audit_event
            where action = 'listing.status_changed' and subject_ids = array[@id]
              and detail->>'to' = 'pending_review' and actor_org_id = @o
            """, ("id", id), ("o", org.Id))).ShouldBe(1);
        (await db.ScalarAsync<long>(
            """
            select count(*) from platform.audit_event
            where action = 'listing.status_changed' and subject_ids = array[@id]
              and detail->>'from' = 'pending_review' and detail->>'to' = 'published'
              and actor_id = @a and actor_org_id is null
            """, ("id", id), ("a", KettleAccounts.OrganiserId))).ShouldBe(1, "an organiser acts for the network, not an organisation");
    }

    [Fact]
    public async Task A_provider_cannot_reach_another_organisation_or_the_review_queue()
    {
        RequireDatabase();
        SeedOrganisation mine = Org("kettle-youth-collective");
        SeedOrganisation theirs = Org("riverside-youth-center");
        Guid theirListing = db.Data.Listings.First(l => l.OrgId == theirs.Id).Id;

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(mine));

        (await provider.GetAsync($"/provider/orgs/{theirs.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await provider.GetAsync($"/provider/orgs/{theirs.Id}/listings/new")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Their listing, addressed through my organisation: not found, not editable.
        (await provider.GetAsync($"/provider/orgs/{mine.Id}/listings/{theirListing}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await provider.GetAsync($"/provider/orgs/{mine.Id}/listings/{theirListing}/preview")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage queue = await provider.GetAsync("/organiser/review");
        queue.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        queue.Headers.Location!.ToString().ShouldContain("/access-denied");
    }

    [Fact]
    public async Task Sending_a_listing_back_needs_a_reason_and_the_provider_sees_it()
    {
        RequireDatabase();
        SeedOrganisation org = Org("westside-arts-studio");

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        Guid id = await CreateAsync(provider, org, "Printmaking for teens", "submit");

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync($"/organiser/review/listings/{id}");

        HttpResponseMessage noReason = await organiser.PostAsync($"/organiser/review/listings/{id}", "changes", ("reason", "  "));
        noReason.StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("needs a reason the provider can read");
        (await StatusAsync(id)).ShouldBe("pending_review");

        (await DecisionEmailsAsync(id, "changes_requested")).ShouldBe("", "a refused decision books nothing");
        await organiser.PostAsync($"/organiser/review/listings/{id}", "changes", ("reason", "Please add the studio's entrance."));
        (await StatusAsync(id)).ShouldBe("changes_requested");
        (await DecisionEmailsAsync(id, "changes_requested")).ShouldBe(await LooksAfterAsync(org.Id));

        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/{id}");
        provider.Html.ShouldContain("Please add the studio&#x27;s entrance.");
        provider.Html.ShouldContain("Save and submit for review", Case.Sensitive, "a provider resubmits after changes");
    }

    [Fact]
    public async Task An_edit_to_what_a_published_listing_says_waits_for_an_organiser_while_its_schedule_goes_live()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-theater-company");
        SeedListing seeded = db.Data.Listings.First(l => l.OrgId == org.Id && l.Status == "published");
        string path = $"/provider/orgs/{org.Id}/listings/{seeded.Id}";
        string review = $"/organiser/review/listings/{seeded.Id}";
        string approvedSummary = await db.ScalarAsync<string>("select summary from opportunity.listing where id = @id", ("id", seeded.Id));

        ListingForm form = await LoadFormAsync(seeded.Id);
        string earlier = TimeOnly.ParseExact(form.StartTime, "HH:mm", CultureInfo.InvariantCulture).AddMinutes(-15).ToString("HH:mm", CultureInfo.InvariantCulture);
        form.Summary = "Now with a free matinee for families.";
        form.StartTime = earlier;

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        await provider.GetAsync(path);
        provider.Html.ShouldContain("wait for an organiser before families see them");
        HttpResponseMessage saved = await provider.PostAsync(path, "listing", Fields(form, "save"));

        // D101: the wording waits; the new time is live at once.
        Location(saved).ShouldEndWith("?saved=held");
        (await StatusAsync(seeded.Id)).ShouldBe("published");
        (await db.ScalarAsync<string>("select summary from opportunity.listing where id = @id", ("id", seeded.Id))).ShouldBe(approvedSummary);
        (await db.ScalarAsync<string>(
            "select to_char(start_time, 'HH24:MI') from opportunity.listing_session where listing_id = @id and not is_cancelled order by starts_on limit 1",
            ("id", seeded.Id))).ShouldBe(earlier);
        (await db.ScalarAsync<string>("select state || ':' || (snapshot->'form'->>'summary') from opportunity.listing_pending_edit where listing_id = @id", ("id", seeded.Id)))
            .ShouldBe("waiting:Now with a free matinee for families.");

        await provider.GetAsync(path);
        provider.Html.ShouldContain("Your changes are waiting for an organiser");
        provider.Html.ShouldContain("Now with a free matinee for families.", Case.Sensitive, "the editor carries on from the waiting edit");

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync("/organiser/review");
        organiser.Html.ShouldContain("Edits waiting for approval");
        organiser.Html.ShouldContain(review);

        // Sent back with a note: the listing is unchanged, and the provider sees why.
        await organiser.GetAsync(review);
        organiser.Html.ShouldContain("Changes waiting for approval");
        organiser.Html.ShouldContain("Now with a free matinee for families.");
        (await organiser.PostAsync(review, "return-edit", ("reason", " "))).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("Say what needs changing");
        await organiser.GetAsync(review);
        Location(await organiser.PostAsync(review, "return-edit", ("reason", "Say which days the matinee runs."))).ShouldEndWith("done=edit_returned");
        string looksAfter = await LooksAfterAsync(org.Id);
        (await DecisionEmailsAsync(seeded.Id, "edit_returned")).ShouldBe(looksAfter);

        await provider.GetAsync(path);
        provider.Html.ShouldContain("An organiser sent your changes back");
        provider.Html.ShouldContain("Say which days the matinee runs.");
        await provider.GetAsync($"/provider/orgs/{org.Id}");
        provider.Html.ShouldContain("Changes sent back:");

        // Saved again, it waits again; approved, it goes live and the listing counts as reviewed.
        form = await LoadFormAsync(seeded.Id);
        form.Summary = "Now with a free Saturday matinee for families.";
        await provider.GetAsync(path);
        Location(await provider.PostAsync(path, "listing", Fields(form, "save"))).ShouldEndWith("?saved=held");
        await organiser.GetAsync(review);
        Location(await organiser.PostAsync(review, "approve-edit")).ShouldEndWith("done=edit_approved");

        (await db.ScalarAsync<string>("select summary from opportunity.listing where id = @id", ("id", seeded.Id))).ShouldBe("Now with a free Saturday matinee for families.");
        (await db.ScalarAsync<long>("select count(*) from opportunity.listing_pending_edit where listing_id = @id", ("id", seeded.Id))).ShouldBe(0);
        (await db.ScalarAsync<bool>("select reviewed_at >= updated_at from opportunity.listing where id = @id", ("id", seeded.Id))).ShouldBeTrue();
        (await DecisionEmailsAsync(seeded.Id, "edit_approved")).ShouldBe(looksAfter);
        (await db.ScalarAsync<string>(
            "select string_agg(action, ',' order by occurred_at) from platform.audit_event where subject_ids = array[@id] and action like 'listing.edit_%'", ("id", seeded.Id)))
            .ShouldBe("listing.edit_held,listing.edit_returned,listing.edit_held,listing.edit_approved");
        await organiser.GetAsync("/organiser/review");
        organiser.Html.ShouldNotContain(review);

        // A discarded edit leaves the approved version. One waiting when the listing is taken
        // down becomes its wording, to be reviewed before it's published again.
        form = await LoadFormAsync(seeded.Id);
        form.Title = "A title nobody approved";
        await provider.GetAsync(path);
        await provider.PostAsync(path, "listing", Fields(form, "save"));
        await provider.GetAsync(path);
        Location(await provider.PostAsync(path, "discard-edit")).ShouldEndWith("?saved=discarded");
        (await db.ScalarAsync<long>("select count(*) from opportunity.listing_pending_edit where listing_id = @id", ("id", seeded.Id))).ShouldBe(0);
        await provider.GetAsync(path);
        provider.Html.ShouldNotContain("A title nobody approved");

        form = await LoadFormAsync(seeded.Id);
        form.Title = "A title for the next season";
        await provider.GetAsync(path);
        await provider.PostAsync(path, "listing", Fields(form, "save"));
        await provider.GetAsync(path);
        await provider.PostAsync(path, "unpublish", ("reason", "Between seasons."));
        (await db.ScalarAsync<string>("select status || ':' || title from opportunity.listing where id = @id", ("id", seeded.Id)))
            .ShouldBe("unpublished:A title for the next season");
        (await DecisionEmailsAsync(seeded.Id, "unpublished")).ShouldBe("", "the provider took it down themselves");
        (await db.ScalarAsync<long>("select count(*) from opportunity.listing_pending_edit where listing_id = @id", ("id", seeded.Id))).ShouldBe(0);
    }

    /// <summary>Who the decision's emails are booked for (D102), in id order.</summary>
    private Task<string> DecisionEmailsAsync(Guid listing, string decision) => db.ScalarAsync<string>(
        """
        select coalesce(string_agg(payload->>'recipientUserId', ',' order by payload->>'recipientUserId'), '')
        from platform.job
        where job_type = 'opportunity.listing_decision_email' and payload->>'listingId' = @l and payload->>'decision' = @d
        """, ("l", listing.ToString()), ("d", decision));

    /// <summary>The organisation's active owners and admins, in id order.</summary>
    private Task<string> LooksAfterAsync(Guid org) => db.ScalarAsync<string>(
        """
        select string_agg(user_id::text, ',' order by user_id::text)
        from directory.org_membership
        where org_id = @o and status = 'active' and ended_at is null and org_role in ('owner', 'admin')
        """, ("o", org));

    private static string Location(HttpResponseMessage response)
    {
        Uri location = response.Headers.Location.ShouldNotBeNull("a redirect");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    [Fact]
    public async Task A_save_against_an_older_version_is_refused()
    {
        RequireDatabase();
        SeedOrganisation org = Org("northgate-music-school");
        SeedListing seeded = db.Data.Listings.First(l => l.OrgId == org.Id && l.Status == "published");
        ListingForm opened = await LoadFormAsync(seeded.Id);

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        string path = $"/provider/orgs/{org.Id}/listings/{seeded.Id}";

        await provider.GetAsync(path);
        opened.Title = "First editor's title";
        (await provider.PostAsync(path, "listing", Fields(opened, "save"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        await provider.GetAsync(path);
        opened.Title = "Second editor's title";
        HttpResponseMessage second = await provider.PostAsync(path, "listing", Fields(opened, "save"));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        provider.Html.ShouldContain("Someone else saved this listing");
        (await db.ScalarAsync<string>("select snapshot->'form'->>'title' from opportunity.listing_pending_edit where listing_id = @id", ("id", seeded.Id)))
            .ShouldBe("First editor's title", "a new title waits for an organiser (D101), and the second save is refused against it");
    }

    [Fact]
    public async Task A_listing_from_an_unapproved_organisation_cannot_be_published()
    {
        RequireDatabase();
        SeedOrganisation org = Org("eastbank-esports-league");
        org.Status.ShouldBe("applied");

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        Guid id = await CreateAsync(provider, org, "Esports tryouts", "submit");

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync($"/organiser/review/listings/{id}");
        await organiser.PostAsync($"/organiser/review/listings/{id}", "publish");

        organiser.Html.ShouldContain("must be approved before its listings can be published");
        (await StatusAsync(id)).ShouldBe("pending_review");
    }

    [Fact]
    public async Task An_organiser_approves_an_organisation_and_the_decision_is_recorded()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-skate-collective");

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        string path = $"/organiser/review/organisations/{org.Id}";

        await organiser.GetAsync(path);
        await organiser.PostAsync(path, "reject", ("reason", ""));
        organiser.Html.ShouldContain("needs a reason the provider can read");

        // Approval is judged on the checklist ticked on the same form.
        await organiser.GetAsync(path);
        (await organiser.PostAsync(path, "checklist", ("action", "approve"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("contact was verified");

        await organiser.GetAsync(path);
        (await organiser.PostAsync(path, "checklist", ("action", "approve"), ("ContactVerified", "true"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        (await db.ScalarAsync<string>("select status from directory.organisation where id = @id", ("id", org.Id))).ShouldBe("approved");
        (await db.ScalarAsync<long>(
            "select count(*) from directory.organisation_vetting_event where org_id = @id and to_status = 'approved' and actor_id = @a",
            ("id", org.Id), ("a", KettleAccounts.OrganiserId))).ShouldBe(1);
        (await db.ScalarAsync<long>(
            """
            select count(*) from platform.audit_event
            where action = 'organisation.status_changed' and subject_kind = 'organisation' and subject_ids = array[@id]
              and detail->>'to' = 'approved' and actor_id = @a
            """, ("id", org.Id), ("a", KettleAccounts.OrganiserId))).ShouldBe(1);
        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'organisation.status_changed' and subject_ids = array[@id] and detail->>'to' = 'rejected'",
            ("id", org.Id))).ShouldBe(0, "a refused decision changed nothing, so it left no change on the trail");
    }

    [Fact]
    public async Task Development_sign_in_does_not_exist_outside_development()
    {
        RequireDatabase();
        await using WebApplicationFactory<Program> production = db.Factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using HttpClient client = production.CreateClient(ConsoleFixture.ClientOptions(cookies: false));

        HttpResponseMessage page = await client.GetAsync(new Uri("/dev/sign-in", UriKind.Relative), TestContext.Current.CancellationToken);
        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var form = new FormUrlEncodedContent([new("userId", SeedIds.For("user/" + Organiser).ToString())]);
        HttpResponseMessage post = await client.PostAsync(new Uri("/dev/sign-in/as", UriKind.Relative), form, TestContext.Current.CancellationToken);
        ((int)post.StatusCode).ShouldBeInRange(400, 499);
        post.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).ShouldBeFalse(string.Join(";", cookies ?? []));
    }

    [Fact]
    public async Task A_suspended_account_is_signed_out_when_its_claims_are_next_checked()
    {
        RequireDatabase();
        SeedOrganisation org = Org("volunteer-kettle");
        string email = Admin(org);
        var clock = new ShiftedClock();

        await using WebApplicationFactory<Program> app = db.Factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<SharedKernel.Time.IClock>(clock)));
        using Browser browser = new(app.CreateClient(ConsoleFixture.ClientOptions()));
        await browser.SignInAsync(email);
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await db.ScalarAsync<int>(
            "with u as (update identity.app_user set status = 'suspended' where id = @id returning 1) select count(*)::int from u",
            ("id", SeedIds.For("user/" + email)));

        // Within the revalidation window the cookie's claims are trusted as issued...
        (await browser.GetAsync("/provider")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and after it, they are rebuilt from the database, which no longer has an active account.
        clock.Shift = ConsoleServicesWindow + TimeSpan.FromMinutes(1);
        HttpResponseMessage after = await browser.GetAsync("/provider");
        after.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        after.Headers.Location!.ToString().ShouldContain("/sign-in");
    }

    private static readonly TimeSpan ConsoleServicesWindow = Auth.ConsoleServices.RevalidateAfter;

    /// <summary>The real clock, moved forward on demand.</summary>
    private sealed class ShiftedClock : SharedKernel.Time.IClock
    {
        public TimeSpan Shift { get; set; }

        public DateTimeOffset UtcNow => SharedKernel.Time.SystemClock.Instance.UtcNow + Shift;

        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public DateOnly TodayIn(TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, timeZone).DateTime);
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Creates a weekly program at the organisation's first site, and returns its id.</summary>
    private async Task<Guid> CreateAsync(Browser browser, SeedOrganisation org, string title, string action)
    {
        Guid site = db.Data.Sites.First(s => s.OrgId == org.Id).Id;
        var form = new ListingForm
        {
            Kind = "program",
            Title = title,
            Summary = $"{title}, every week.",
            PrimaryLanguage = "en",
            TermIds = [KettleTaxonomy.TermId("category", "arts")],
            SiteIds = [site],
            Modality = "in_person",
            CostModel = "free",
            RegistrationMode = "external",
            ExternalUrl = "https://example.org/kettle/test",
            Repeats = "weekly",
            Days = ["WE"],
            StartsOn = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            EndsOn = DateTime.UtcNow.AddDays(70).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            StartTime = "16:00",
            EndTime = "17:30",
        };

        string path = $"/provider/orgs/{org.Id}/listings/new";
        await browser.GetAsync(path);
        HttpResponseMessage response = await browser.PostAsync(path, "listing", Fields(form, action));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);

        Uri redirect = response.Headers.Location!;
        string location = redirect.IsAbsoluteUri ? redirect.PathAndQuery : redirect.ToString();
        Guid id = Guid.Parse(location.Split('/')[^1].Split('?')[0]);
        await browser.GetAsync(location);
        return id;
    }

    private async Task<ListingForm> LoadFormAsync(Guid listingId)
    {
        await using AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
        EditableListing listing = (await scope.ServiceProvider.GetRequiredService<ListingEditor>().LoadAsync(listingId, TestContext.Current.CancellationToken))!;
        return listing.Form;
    }

    private Task<string> StatusAsync(Guid id) =>
        db.ScalarAsync<string>("select status from opportunity.listing where id = @id", ("id", id));

    internal static (string, string)[] Fields(ListingForm f, string action)
    {
        List<(string, string)> fields =
        [
            ("action", action), ("Version", f.Version), ("Kind", f.Kind), ("Title", f.Title), ("Summary", f.Summary),
            ("Description", f.Description), ("PrimaryLanguage", f.PrimaryLanguage), ("AgeMin", f.AgeMin), ("AgeMax", f.AgeMax),
            ("GradeMin", f.GradeMin), ("GradeMax", f.GradeMax), ("Modality", f.Modality), ("VirtualUrl", f.VirtualUrl),
            ("CostModel", f.CostModel), ("CostMin", f.CostMin), ("CostMax", f.CostMax), ("SlidingScaleNote", f.SlidingScaleNote),
            ("RegistrationMode", f.RegistrationMode), ("ExternalUrl", f.ExternalUrl), ("Capacity", f.Capacity),
            ("TransportNote", f.TransportNote), ("AccessibilityNote", f.AccessibilityNote), ("ContactEmail", f.ContactEmail),
            ("ContactPhone", f.ContactPhone), ("Repeats", f.Repeats), ("StartsOn", f.StartsOn), ("EndsOn", f.EndsOn),
            ("StartTime", f.StartTime), ("EndTime", f.EndTime), ("DeadlineKind", f.DeadlineKind), ("DeadlineOn", f.DeadlineOn),
            ("Interval", f.Interval), ("SkipDates", f.SkipDates), ("ShowFrom", f.ShowFrom), ("TakeDownAfter", f.TakeDownAfter),
        ];

        fields.AddRange(f.TermIds.Select(t => ("TermIds", t.ToString())));
        fields.AddRange(f.AccommodationIds.Select(t => ("AccommodationIds", t.ToString())));
        fields.AddRange(f.SiteIds.Select(t => ("SiteIds", t.ToString())));
        fields.AddRange(f.Languages.Select(t => ("Languages", t)));
        fields.AddRange(f.Days.Select(t => ("Days", t)));
        if (f.MealsProvided)
        {
            fields.Add(("MealsProvided", "true"));
        }

        if (f.TransportProvided)
        {
            fields.Add(("TransportProvided", "true"));
        }

        if (f.IsDropIn)
        {
            fields.Add(("IsDropIn", "true"));
        }

        return [.. fields];
    }
}
