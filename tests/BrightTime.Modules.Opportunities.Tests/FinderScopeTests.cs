using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>
/// The finder's newer scopes: one organisation's listings, a fixed set of listings (a collection
/// or a saved list), and counts by neighbourhood for the map. Builder and in-memory finder agree.
/// </summary>
public sealed class FinderScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = KettleCatalogue.TenantId;

    private static IReadOnlyList<ListingSearchRow> Rows => KettleCatalogue.Rows(new FixedClock(Now));

    private static InMemoryListingFinder Finder => new(Rows, new FixedClock(Now));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_organisation_and_a_set_of_listings_are_bound_values_never_text()
    {
        Guid org = Guid.NewGuid();
        Guid listing = Guid.NewGuid();

        BuiltQuery built = SearchQueryBuilder.Build(new SearchQuery { TenantId = Tenant, OrganisationId = org, ListingIds = [listing] });

        built.Sql.ShouldContain("ls.org_id = @org_id");
        built.Sql.ShouldContain("ls.listing_id = any(@listing_ids)");
        built.Sql.ShouldNotContain(org.ToString());
        built.Parameters["org_id"].ShouldBe(org);
        built.Parameters["listing_ids"].ShouldBe(new[] { listing });
    }

    [Fact]
    public void An_empty_set_of_listings_still_filters_so_it_matches_nothing()
    {
        BuiltQuery built = SearchQueryBuilder.Build(new SearchQuery { TenantId = Tenant, ListingIds = [] });

        built.Sql.ShouldContain("any(@listing_ids)");
    }

    [Fact]
    public void Neighbourhood_counts_use_the_search_filters_and_keep_online_rows_apart()
    {
        BuiltQuery built = SearchQueryBuilder.BuildNeighbourhoodCounts(new SearchQuery
        {
            TenantId = Tenant,
            Kinds = ["program"],
            Text = "robots'; drop table x; --",
            FreeOnly = true,
        });

        built.Sql.ShouldContain("ls.site_id is null as online");
        built.Sql.ShouldContain("count(distinct ls.listing_id)");
        built.Sql.ShouldContain("group by 1, 2");
        built.Sql.ShouldContain("ls.kind = any(@kinds)");
        built.Sql.ShouldContain("ls.is_free");
        built.Sql.ShouldNotContain("drop table");
        built.Parameters["text"].ShouldBe("robots'; drop table x; --");
    }

    [Fact]
    public async Task One_organisations_page_shows_only_its_listings()
    {
        Guid org = Rows[0].OrganisationId;

        FinderPage page = await Finder.SearchAsync(new SearchQuery { TenantId = Tenant, OrganisationId = org, PageSize = new PageSize(50) }, Ct);

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(i => i.OrganisationId.Value == org);
    }

    [Fact]
    public async Task A_set_of_listings_returns_those_published_and_nothing_else()
    {
        Guid[] published = [.. Rows.Where(r => r.IsPublished).Select(r => r.ListingId).Distinct().Take(2)];
        Guid unknown = Guid.NewGuid();

        FinderPage page = await Finder.SearchAsync(new SearchQuery { TenantId = Tenant, ListingIds = [.. published, unknown] }, Ct);

        page.Items.Select(i => i.ListingId.Value).Order().ShouldBe(published.Order());
    }

    [Fact]
    public async Task Counts_add_up_by_neighbourhood_with_online_listings_apart()
    {
        NeighbourhoodCounts counts = await Finder.CountByNeighbourhoodAsync(new SearchQuery { TenantId = Tenant }, Ct);

        int expectedOnline = Rows.Where(r => r.IsPublished && r.SiteId is null).Select(r => r.ListingId).Distinct().Count();
        counts.OnlineOnly.ShouldBe(expectedOnline);
        counts.ByNeighbourhood.ShouldNotBeEmpty();
        foreach ((Guid hood, int n) in counts.ByNeighbourhood)
        {
            n.ShouldBe(Rows.Where(r => r.IsPublished && r.SiteId is not null && r.NeighbourhoodId == hood).Select(r => r.ListingId).Distinct().Count());
        }
    }

    [Fact]
    public async Task A_listing_card_knows_its_next_deadline()
    {
        FinderPage page = await Finder.SearchAsync(new SearchQuery { TenantId = Tenant, PageSize = new PageSize(50) }, Ct);

        page.Items.ShouldContain(i => i.NextDeadlineAt != null, "the sample has listings with deadlines");
    }
}
