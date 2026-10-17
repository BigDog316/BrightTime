using System.Text.RegularExpressions;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Seed.Tests;

/// <summary>
/// The catalogue itself, with no database: the M1 counts, determinism, and the schema's CHECK
/// constraints restated so a bad template fails here, by name, rather than as a 23514 mid-seed.
/// </summary>
public sealed partial class KettleSeedTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private static readonly SeedData Data = KettleSeed.Build(Today);

    [Fact]
    public void The_counts_are_the_ones_M1_specifies()
    {
        Data.Areas.Count(a => a.Kind == "municipality").ShouldBe(11);
        Data.Areas.Count(a => a.Kind == "neighbourhood").ShouldBe(18);
        Data.Districts.Count.ShouldBe(4);
        Data.Schools.Count.ShouldBe(28);
        Data.Organisations.Count.ShouldBe(60);
        Data.Sites.Count.ShouldBe(85);
        Data.Listings.Count.ShouldBe(420);
    }

    [Fact]
    public void Every_kind_is_well_represented()
    {
        Dictionary<string, int> byKind = Data.Listings.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => g.Count());

        byKind["program"].ShouldBeGreaterThan(200);
        byKind["event"].ShouldBeGreaterThanOrEqualTo(40);
        byKind["job"].ShouldBeGreaterThanOrEqualTo(30);
        byKind["resource"].ShouldBeGreaterThanOrEqualTo(30);
    }

    [Fact]
    public void Every_workflow_state_has_rows_and_most_are_published()
    {
        Dictionary<string, int> byStatus = Data.Listings.GroupBy(l => l.Status).ToDictionary(g => g.Key, g => g.Count());

        byStatus["published"].ShouldBeGreaterThan(370);
        byStatus.Keys.ShouldBe(["draft", "pending_review", "changes_requested", "unpublished", "published"], ignoreOrder: true);
        Data.Organisations.Select(o => o.Status).Distinct()
            .ShouldBe(["approved", "applied", "under_review", "changes_requested"], ignoreOrder: true);
    }

    [Fact]
    public void The_same_day_produces_the_same_catalogue()
    {
        SeedData again = KettleSeed.Build(Today);

        again.Listings.Select(l => (l.Id, l.Slug, l.Status, l.Sessions[0].StartsOn, l.Sessions[0].Rrule))
            .ShouldBe(Data.Listings.Select(l => (l.Id, l.Slug, l.Status, l.Sessions[0].StartsOn, l.Sessions[0].Rrule)));
        again.Sites.Select(s => (s.Id, s.Lon, s.Lat)).ShouldBe(Data.Sites.Select(s => (s.Id, s.Lon, s.Lat)));
    }

    [Fact]
    public void Identifiers_and_slugs_are_unique()
    {
        IEnumerable<Guid> ids = Data.Areas.Select(a => a.Id)
            .Concat(Data.Districts.Select(d => d.Id))
            .Concat(Data.Schools.Select(s => s.Id))
            .Concat(Data.Terms.Select(t => t.Id))
            .Concat(Data.Organisations.Select(o => o.Id))
            .Concat(Data.Sites.Select(s => s.Id))
            .Concat(Data.Listings.Select(l => l.Id))
            .Concat(Data.Listings.SelectMany(l => l.Sessions.Select(s => s.Id)))
            .Concat(Data.Listings.SelectMany(l => l.Deadlines.Select(d => d.Id)));

        ids.ShouldBeUnique();
        Data.Listings.Select(l => l.Slug.ToUpperInvariant()).ShouldBeUnique("ux_listing_slug is case-insensitive (citext)");
        Data.Organisations.Select(o => o.Slug).ShouldBeUnique();
        Data.Schools.Select(s => s.Name).ShouldBeUnique("ux_school_name");
    }

    [Fact]
    public void Listings_satisfy_the_listing_table_constraints()
    {
        foreach (SeedListing l in Data.Listings)
        {
            string at = $"{l.Slug}: ";

            (l.Status == "published").ShouldBe(l.PublishedAt is not null, at + "ck_listing_published");
            (l.RegistrationMode != "external" || l.ExternalUrl!.StartsWith("https://", StringComparison.Ordinal)).ShouldBeTrue(at + "ck_listing_external_url");
            (l.RegistrationMode != "builtin" || l.CapacityTotal > 0).ShouldBeTrue(at + "ck_listing_builtin_capacity");
            (l.Kind != "job" || l.CostModel is "stipend_paid" or "free").ShouldBeTrue(at + "ck_listing_job_is_paid");
            (l.Kind != "resource" || l.RegistrationMode == "none").ShouldBeTrue(at + "ck_listing_resource_dropin");
            (l.AgeMin is null || l.AgeMax is null || l.AgeMin <= l.AgeMax).ShouldBeTrue(at + "ck_listing_ages");

            switch (l.CostModel)
            {
                case "free":
                    l.CostCentsMin.ShouldBeNull(at + "ck_listing_cost");
                    l.CostCentsMax.ShouldBeNull(at + "ck_listing_cost");
                    break;
                case "fixed":
                    l.CostCentsMin.ShouldNotBeNull(at + "ck_listing_cost");
                    l.CostCentsMax.ShouldBeNull(at + "ck_listing_cost");
                    break;
                case "sliding_scale":
                    l.CostCentsMax!.Value.ShouldBeGreaterThanOrEqualTo(l.CostCentsMin!.Value, at + "ck_listing_cost");
                    break;
            }

            l.Slug.Length.ShouldBeLessThanOrEqualTo(SharedKernel.Primitives.Slug.MaxLength + 4);
            (l.Modality == "virtual").ShouldBe(l.SiteIds.Count == 0, at + "online listings have no site, in-person ones do");
        }
    }

    [Fact]
    public void Every_published_listing_has_an_upcoming_session()
    {
        // The expander the projection uses, over the projection's horizon. A published listing
        // with nothing upcoming would be invisible to every date filter.
        DateOnly horizon = Today.AddDays(180);
        foreach (SeedListing l in Data.Listings.Where(l => l.Status == "published"))
        {
            SeedSession s = l.Sessions.ShouldHaveSingleItem();
            var rule = new SessionRule(KettleListings.Zone, s.StartsOn, s.EndsOn, s.StartTime, s.EndTime, s.Rrule, s.Exdates);
            DateOnly from = s.StartsOn > Today.AddDays(1) ? s.StartsOn : Today.AddDays(1);

            OccurrenceExpander.Expand(rule, from, horizon).ShouldNotBeEmpty(l.Slug);
        }
    }

    [Fact]
    public void A_weekly_session_starts_on_a_day_its_rule_names()
    {
        // RFC 5545 counts DTSTART as an occurrence even when the rule does not match it.
        foreach (SeedSession s in Data.Listings.SelectMany(l => l.Sessions).Where(s => s.Rrule?.Contains("WEEKLY", StringComparison.Ordinal) == true))
        {
            string day = s.StartsOn.DayOfWeek.ToString()[..2].ToUpperInvariant();
            s.Rrule!.ShouldContain(day);
        }
    }

    [Fact]
    public void Every_site_lies_inside_its_neighbourhood_and_the_county()
    {
        foreach (SeedSite site in Data.Sites)
        {
            Box box = Data.Areas.Single(a => a.Id == site.NeighbourhoodId).Box!.Value;
            site.Lon.ShouldBeInRange(box.West, box.East);
            site.Lat.ShouldBeInRange(box.South, box.North);
        }
    }

    [Fact]
    public void Contact_details_are_fictional_by_construction()
    {
        foreach (SeedOrganisation o in Data.Organisations)
        {
            new Uri(o.WebsiteUrl).Host.ShouldBe("example.org");
            o.PublicEmail.ShouldEndWith(".example.org");
            FictionalPhone().IsMatch(o.PublicPhone).ShouldBeTrue(o.PublicPhone);
        }

        Data.Listings.Where(l => l.ExternalUrl is not null)
            .ShouldAllBe(l => new Uri(l.ExternalUrl!).Host == "example.org");
    }

    [Fact]
    public void Some_sites_keep_their_address_private()
    {
        Data.Sites.Count(s => !s.IsAddressPublic).ShouldBeGreaterThanOrEqualTo(4);
        Data.Listings.Count(l => l.Status == "published" && l.SiteIds.Any(id => !Data.Sites.Single(s => s.Id == id).IsAddressPublic))
            .ShouldBeGreaterThan(0, "the page's private-address path needs published examples");
    }

    [Fact]
    public void A_representative_subset_is_in_all_six_locales()
    {
        string[] locales = ["en", "es", "zh-Hans", "bn", "ht", "ar"];

        KettleTemplates.All.Count(t => t.IsSixLocale).ShouldBeGreaterThanOrEqualTo(12);
        KettleTemplates.All.ShouldAllBe(t => t.Text.ContainsKey("es"));

        int sixLocale = Data.Listings.Count(l =>
            locales.All(loc => l.PrimaryLanguage == loc || l.Translations.Any(t => t.Locale == loc)));
        sixLocale.ShouldBeGreaterThan(60);

        Data.Terms.ShouldAllBe(t => locales.All(t.Names.ContainsKey));
    }

    [Fact]
    public void Some_listings_are_written_in_another_language_first()
    {
        Data.Listings.Count(l => l.PrimaryLanguage != "en").ShouldBeGreaterThan(5);
        Data.Listings.Where(l => l.PrimaryLanguage != "en")
            .ShouldAllBe(l => l.Translations.Any(t => t.Locale == "en"), "an English reader can still read it");
    }

    [Fact]
    public void Seed_ids_are_stable_version_7_shapes()
    {
        Guid id = SeedIds.For("anything");

        id.ShouldBe(SeedIds.For("anything"));
        id.ShouldNotBe(SeedIds.For("anything else"));
        id.Version.ShouldBe(7);
        (id.Variant & 0b1100).ShouldBe(0b1000);
    }

    [GeneratedRegex(@"^\(555\) 555-01\d\d$")]
    private static partial Regex FictionalPhone();
}
