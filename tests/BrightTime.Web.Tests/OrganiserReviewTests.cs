using System.Globalization;
using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The organiser's side: the vetting checklist, approval and suspension, the diff a
/// resubmitted listing is reviewed against, and the taxonomy. Each test uses an organisation
/// or terms no other test touches.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class OrganiserReviewTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private SeedOrganisation Org(string slug) => db.Data.Organisations.Single(o => o.Slug == slug);

    private static string Admin(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    // ---- vetting ---------------------------------------------------------------------------

    [Fact]
    public async Task The_checklist_is_saved_between_visits_without_deciding()
    {
        RequireDatabase();
        SeedOrganisation org = Org("grayhaven-young-builders");
        string path = $"/organiser/review/organisations/{org.Id}";
        string expires = Day(DateTime.UtcNow.AddYears(1));
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);

        await organiser.GetAsync(path);
        HttpResponseMessage saved = await organiser.PostAsync(path, "checklist",
            ("action", "save"), ("ContactVerified", "true"), ("SafetyPolicySeen", "true"), ("InsuranceExpiresOn", expires));

        Location(saved).ShouldBe($"{path}?saved=1");
        (await db.ScalarAsync<string>("select status from directory.organisation where id = @o", ("o", org.Id))).ShouldBe("under_review");
        (await db.ScalarAsync<string>("select insurance_expires_on::text from directory.organisation where id = @o", ("o", org.Id))).ShouldBe(expires);
        (await db.ScalarAsync<string>(
            """
            select from_status || '>' || to_status || ':' || (checklist->>'contact_verified') || ':' || (checklist->>'website_checked')
              from directory.organisation_vetting_event where org_id = @o order by occurred_at desc limit 1
            """, ("o", org.Id))).ShouldBe("under_review>under_review:true:false");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'organisation.checklist_saved' and subject_ids = array[@o]", ("o", org.Id)))
            .ShouldBe(1);

        await organiser.GetAsync(path);
        organiser.Html.ShouldMatch("name=\"ContactVerified\" value=\"true\" checked");
        organiser.Html.ShouldNotMatch("name=\"WebsiteChecked\" value=\"true\" checked");
        organiser.Html.ShouldContain($"value=\"{expires}\"");
    }

    [Fact]
    public async Task Where_the_tenant_requires_insurance_approval_waits_for_a_current_certificate_and_suspension_is_queued_for_search()
    {
        RequireDatabase();
        SeedOrganisation org = Org("wexley-chess-club");
        string path = $"/organiser/review/organisations/{org.Id}";
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);

        await db.ScalarAsync<int>(
            "insert into platform.feature_flag (tenant_id, key, is_enabled) values (@t, 'vetting.insurance_required', true) on conflict (tenant_id, key) do update set is_enabled = true returning 1",
            ("t", KettleSeed.TenantId));
        try
        {
            await organiser.GetAsync(path);
            organiser.Html.ShouldContain("Required: approval needs one that has not expired.");
            HttpResponseMessage refused = await organiser.PostAsync(path, "checklist",
                ("action", "approve"), ("ContactVerified", "true"), ("InsuranceExpiresOn", Day(DateTime.UtcNow.AddDays(-2))));
            refused.StatusCode.ShouldBe(HttpStatusCode.OK);
            organiser.Html.ShouldContain("certificate of insurance is needed before approval");
            (await db.ScalarAsync<string>("select status from directory.organisation where id = @o", ("o", org.Id))).ShouldBe("under_review");

            await organiser.GetAsync(path);
            HttpResponseMessage approved = await organiser.PostAsync(path, "checklist",
                ("action", "approve"), ("ContactVerified", "true"), ("InsuranceExpiresOn", Day(DateTime.UtcNow.AddYears(1))));
            Location(approved).ShouldBe("/organiser/review?done=approved");
        }
        finally
        {
            await db.ScalarAsync<int>("delete from platform.feature_flag where tenant_id = @t and key = 'vetting.insurance_required' returning 1",
                ("t", KettleSeed.TenantId));
        }

        (await db.ScalarAsync<string>(
            "select checklist->>'contact_verified' from directory.organisation_vetting_event where org_id = @o and to_status = 'approved'",
            ("o", org.Id))).ShouldBe("true", "the approval records the checklist it was judged on");

        await organiser.GetAsync(path);
        HttpResponseMessage suspended = await organiser.PostAsync(path, "suspend", ("reason", "Complaint under investigation."));
        Location(suspended).ShouldBe("/organiser/review?done=suspended");
        (await db.ScalarAsync<long>("select count(*) from platform.job where unique_key = @k",
            ("k", $"directory.organisation_changed:{org.Id:N}"))).ShouldBe(1, "approval and suspension collapse into one waiting job");
    }

    // ---- listing diff ----------------------------------------------------------------------

    [Fact]
    public async Task A_resubmitted_listing_is_reviewed_against_what_the_organiser_last_saw()
    {
        RequireDatabase();
        SeedOrganisation org = Org("kettle-county-youth-orchestra");
        Guid site = db.Data.Sites.First(s => s.OrgId == org.Id).Id;
        var form = new ListingForm
        {
            Title = "String quartet coaching",
            Summary = "Weekly coaching for young quartets.",
            TermIds = [KettleTaxonomy.TermId("category", "arts")],
            SiteIds = [site],
            ExternalUrl = "https://example.org/kettle/quartet",
            Days = ["MO"],
            StartsOn = Day(DateTime.UtcNow.AddDays(10)),
            EndsOn = Day(DateTime.UtcNow.AddDays(80)),
            StartTime = "16:00",
            EndTime = "17:30",
        };
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        string newPath = $"/provider/orgs/{org.Id}/listings/new";
        await provider.GetAsync(newPath);
        (await provider.PostAsync(newPath, "listing", ConsoleTests.Fields(form, "submit"))).StatusCode.ShouldBe(HttpStatusCode.Redirect, provider.Html);
        Guid id = await db.ScalarAsync<Guid>("select id from opportunity.listing where org_id = @o and title = @t", ("o", org.Id), ("t", form.Title));

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        string reviewPath = $"/organiser/review/listings/{id}";
        await organiser.GetAsync(reviewPath);
        organiser.Html.ShouldContain("has not been reviewed before");
        await organiser.PostAsync(reviewPath, "changes", ("reason", "Say which instruments."));

        // The provider changes the title and the day, and sends it back.
        ListingForm edited = await LoadFormAsync(id);
        edited.Title = "String quartet coaching for violin, viola and cello";
        edited.Days.Clear();
        edited.Days.Add("WE");
        string editPath = $"/provider/orgs/{org.Id}/listings/{id}";
        await provider.GetAsync(editPath);
        (await provider.PostAsync(editPath, "listing", ConsoleTests.Fields(edited, "submit"))).StatusCode.ShouldBe(HttpStatusCode.Redirect, provider.Html);

        await organiser.GetAsync(reviewPath);
        organiser.Html.ShouldContain("Changes since the last review");
        organiser.Html.ShouldContain("fields changed since the review");
        organiser.Html.ShouldContain(">String quartet coaching<");
        organiser.Html.ShouldContain(">String quartet coaching for violin, viola and cello<");
        organiser.Html.ShouldContain("Wednesday");

        (await db.ScalarAsync<string>(
            "select string_agg(kind, ',' order by created_at) from opportunity.listing_revision where listing_id = @l", ("l", id)))
            .ShouldBe("submitted,reviewed,submitted");
    }

    // ---- taxonomy --------------------------------------------------------------------------

    [Fact]
    public async Task An_organiser_adds_and_renames_a_skill_with_names_in_several_languages()
    {
        RequireDatabase();
        string code = "drone-piloting-" + Guid.NewGuid().ToString("N")[..6];
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);

        string path = "/organiser/taxonomy/skill/new";
        await organiser.GetAsync(path);
        HttpResponseMessage noEnglish = await organiser.PostAsync(path, "term", ("Code", code), ("Name_es", "Pilotaje de drones"));
        noEnglish.StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("Give the term an English name.");

        await organiser.GetAsync(path);
        HttpResponseMessage created = await organiser.PostAsync(path, "term", ("Code", code), ("Name_en", "Drone piloting"), ("Name_es", "Pilotaje de drones"));
        Location(created).ShouldBe("/organiser/taxonomy?vocabulary=skill&done=saved");
        Guid term = await db.ScalarAsync<Guid>("select id from platform.taxonomy_term where vocabulary = 'skill' and code = @c", ("c", code));
        (await db.ScalarAsync<string>("select string_agg(locale || '=' || name, ',' order by locale) from platform.taxonomy_term_name where term_id = @t", ("t", term)))
            .ShouldBe("en=Drone piloting,es=Pilotaje de drones");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'taxonomy.term_created' and subject_ids = array[@t]", ("t", term))).ShouldBe(1);

        await organiser.GetAsync(path);
        await organiser.PostAsync(path, "term", ("Code", code), ("Name_en", "Again"));
        organiser.Html.ShouldContain("is already used in this vocabulary");

        string editPath = $"/organiser/taxonomy/skill/{term}";
        await organiser.GetAsync(editPath);
        organiser.Html.ShouldNotContain("id=\"Code\"", Case.Sensitive, "a code cannot change once made");
        HttpResponseMessage renamed = await organiser.PostAsync(editPath, "term", ("Name_en", "Flying drones"), ("SortOrder", "5"));
        Location(renamed).ShouldBe("/organiser/taxonomy?vocabulary=skill&done=saved");
        (await db.ScalarAsync<string>("select name from platform.taxonomy_term_name where term_id = @t and locale = 'en'", ("t", term))).ShouldBe("Flying drones");
        (await db.ScalarAsync<long>("select count(*) from platform.job where unique_key = @k", ("k", $"platform.term_changed:{term:N}"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_interest_is_offered_to_providers_until_it_is_retired()
    {
        RequireDatabase();
        string code = "model-rocketry-" + Guid.NewGuid().ToString("N")[..6];
        string name = "Model rocketry " + code[^6..];
        Guid stem = KettleTaxonomy.TermId("category", "stem");
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);

        string path = "/organiser/taxonomy/interest/new";
        await organiser.GetAsync(path);
        await organiser.PostAsync(path, "term", ("Code", code), ("Name_en", name));
        organiser.Html.ShouldContain("Choose the category this interest belongs to.");
        await organiser.GetAsync(path);
        await organiser.PostAsync(path, "term", ("Code", code), ("ParentId", stem.ToString()), ("Name_en", name));
        Guid term = await db.ScalarAsync<Guid>("select id from platform.taxonomy_term where vocabulary = 'interest' and code = @c", ("c", code));

        SeedOrganisation org = Org("kettle-county-youth-orchestra");
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(org));
        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/new");
        provider.Html.ShouldContain(name);

        // A category with active interests is not retired; the interest is.
        await organiser.GetAsync("/organiser/taxonomy?vocabulary=category");
        await organiser.PostAsync("/organiser/taxonomy?vocabulary=category", "state-" + stem.ToString("N"), ("term", stem.ToString()));
        organiser.Html.ShouldContain("Retire or move this category");
        (await db.ScalarAsync<bool>("select is_active from platform.taxonomy_term where id = @t", ("t", stem))).ShouldBeTrue();

        await organiser.GetAsync("/organiser/taxonomy?vocabulary=interest");
        HttpResponseMessage retired = await organiser.PostAsync("/organiser/taxonomy?vocabulary=interest", "state-" + term.ToString("N"), ("term", term.ToString()));
        Location(retired).ShouldBe("/organiser/taxonomy?vocabulary=interest&done=retired");

        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/new");
        provider.Html.ShouldNotContain(name);
    }

    [Fact]
    public async Task Providers_cannot_manage_the_taxonomy()
    {
        RequireDatabase();
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync(Admin(Org("kettle-county-youth-orchestra")));

        HttpResponseMessage response = await provider.GetAsync("/organiser/taxonomy");

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    private async Task<ListingForm> LoadFormAsync(Guid listingId)
    {
        await using AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
        return (await scope.ServiceProvider.GetRequiredService<ListingEditor>().LoadAsync(listingId, TestContext.Current.CancellationToken))!.Form;
    }

    private static string Location(HttpResponseMessage response)
    {
        Uri location = response.Headers.Location.ShouldNotBeNull("a redirect");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }
}
