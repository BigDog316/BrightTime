using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Paging;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>
/// What the finder's SQL contains, and what it must never contain. The injection
/// test is the one to keep: user text is bound, never spliced.
/// </summary>
public sealed class QueryBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("0196c3e8-0000-7000-8000-0000000000ee");

    private static readonly DateTimeOffset At = new(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);

    private static SearchQuery Browse(SearchSort sort = SearchSort.Relevance) => new() { TenantId = Tenant, Sort = sort, AsOf = At };

    [Fact]
    public void A_bare_browse_has_no_optional_predicate_at_all()
    {
        // The reason the builder composes rather than guards with (:p is null or …):
        // a dead branch is a predicate the planner still costs.
        BuiltQuery query = SearchQueryBuilder.Build(Browse());

        query.Sql.ShouldNotContain("ls.location &&");
        query.Sql.ShouldNotContain("websearch_to_tsquery");
        query.Sql.ShouldNotContain("facets @>");
        query.Sql.ShouldNotContain("tstzrange");
        query.Sql.ShouldNotContain("cost_cents <=");
        query.Sql.ShouldNotContain("and ls.is_free");
    }

    [Fact]
    public void Every_query_is_scoped_to_the_tenant_and_to_published_rows()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse());

        query.Sql.ShouldContain("ls.tenant_id = @tenant_id and ls.is_published");
        query.Parameters["tenant_id"].ShouldBe(Tenant);
    }

    [Fact]
    public void A_later_page_scores_at_the_instant_its_cursor_carries()
    {
        var first = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with { After = new Cursor("0.5", Guid.CreateVersion7(), first) });

        query.Sql.ShouldNotContain("now()");
        query.Parameters["as_of"].ShouldBe(first);
    }

    [Fact]
    public void A_text_search_adds_the_text_predicate_and_binds_the_text()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with { Text = "robotics", TextSearchConfig = "platform.ts_en" });

        query.Sql.ShouldContain("websearch_to_tsquery(@ts_config::regconfig, @text)");
        query.Sql.ShouldContain("@text <<% ls.fuzzy_text");
        query.Parameters["text"].ShouldBe("robotics");
        query.Parameters["ts_config"].ShouldBe("platform.ts_en");
    }

    [Fact]
    public void Hostile_text_never_reaches_the_sql_text()
    {
        // The string a classic injection would use. It must appear only as a bound
        // value, never as part of the statement.
        const string hostile = "x'; drop table opportunity.listing_search; --";

        BuiltQuery query = SearchQueryBuilder.Build(Browse() with { Text = hostile });

        query.Sql.ShouldNotContain("drop table");
        query.Sql.ShouldNotContain("x'");
        query.Parameters["text"].ShouldBe(hostile);
    }

    [Fact]
    public void Every_bound_value_is_a_parameter_and_none_is_inlined()
    {
        // Set every filter with a distinctive value, then check no value leaks into SQL.
        var query = new SearchQuery
        {
            TenantId = Tenant,
            AsOf = At,
            Text = "UNIQUE_TEXT_TOKEN",
            AllFacets = ["UNIQUE_FACET_A"],
            AnyFacets = ["UNIQUE_FACET_B"],
            Kinds = ["program"],
            WindowStart = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            WindowEnd = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            MaxCostCents = 4321,
            Origin = new Coordinate(41.0345, -73.7629),
            Radius = Distance.FromMetres(7777),
            InterestTokens = ["UNIQUE_INTEREST"],
        };

        BuiltQuery built = SearchQueryBuilder.Build(query);

        foreach (string marker in new[] { "UNIQUE_TEXT_TOKEN", "UNIQUE_FACET_A", "UNIQUE_FACET_B", "UNIQUE_INTEREST", "4321", "7777", "41.0345" })
        {
            built.Sql.ShouldNotContain(marker);
        }

        built.Parameters.Values.ShouldContain("UNIQUE_TEXT_TOKEN");
        built.Parameters["max_cost_cents"].ShouldBe(4321);
        built.Parameters["radius_m"].ShouldBe(7777);
    }

    [Fact]
    public void An_origin_adds_distance_and_radius_predicates_bound_by_coordinate()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with
        {
            Origin = new Coordinate(41.0345, -73.7629),
            Radius = Distance.FromMetres(5000),
        });

        query.Sql.ShouldContain("ls.location && st_makeenvelope(@box_west, @box_south, @box_east, @box_north, 4326)::geography");
        query.Sql.ShouldContain("st_distance(ls.location, st_setsrid(st_makepoint(@origin_lon, @origin_lat), 4326)::geography) <= @radius_m");
        query.Sql.ShouldNotContain("st_dwithin", Case.Insensitive, "PostGIS misestimates it; see SearchQueryBuilder");
        ((double)query.Parameters["box_south"]!).ShouldBeLessThan(41.0345);
        ((double)query.Parameters["box_north"]!).ShouldBeGreaterThan(41.0345);
        query.Parameters["origin_lat"].ShouldBe(41.0345);
        query.Parameters["origin_lon"].ShouldBe(-73.7629);
    }

    [Fact]
    public void Without_an_origin_one_row_per_listing_is_read_and_nothing_is_collapsed()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse(SearchSort.Soonest) with { AllFacets = ["hood:1", "cat:stem"] });

        query.Sql.ShouldContain("and ls.is_primary");
        query.Sql.ShouldNotContain("distinct on");
    }

    [Fact]
    public void With_an_origin_every_site_is_read_and_each_listing_shown_at_its_nearest()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with { Origin = new Coordinate(41.0345, -73.7629), Radius = Distance.FromMetres(5000) });

        query.Sql.ShouldNotContain("is_primary");
        query.Sql.ShouldContain("distinct on (listing_id)");
    }

    [Theory]
    [InlineData(41.0345, -73.7629, 5_000)]
    [InlineData(0.5, 179.9, 80_000)]
    [InlineData(60.2, 24.9, 80_000)]
    [InlineData(-33.9, 151.2, 100)]
    public void The_distance_box_contains_the_whole_circle(double lat, double lon, int metres)
    {
        (double west, double south, double east, double north) = SearchQueryBuilder.BoundingBox(new Coordinate(lat, lon), Distance.FromMetres(metres));

        for (int bearing = 0; bearing < 360; bearing += 5)
        {
            (double pLat, double pLon) = Destination(lat, lon, bearing, metres);
            pLat.ShouldBeInRange(south, north);
            if (east < 180 && west > -180)
            {
                pLon.ShouldBeInRange(west, east);
            }
        }
    }

    /// <summary>The point a distance away on a bearing, on a sphere: enough to check a box with a one percent margin.</summary>
    private static (double Lat, double Lon) Destination(double lat, double lon, double bearing, double metres)
    {
        const double R = 6_371_008.8;
        double d = metres / R, b = bearing * Math.PI / 180, p1 = lat * Math.PI / 180, l1 = lon * Math.PI / 180;
        double p2 = Math.Asin((Math.Sin(p1) * Math.Cos(d)) + (Math.Cos(p1) * Math.Sin(d) * Math.Cos(b)));
        double l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - (Math.Sin(p1) * Math.Sin(p2)));
        return (p2 * 180 / Math.PI, l2 * 180 / Math.PI);
    }

    [Fact]
    public void Nearest_without_an_origin_falls_back_to_relevance_rather_than_erroring()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse(SearchSort.Nearest));

        query.Sql.ShouldContain("order by s.score desc, s.listing_id desc");
        query.Sql.ShouldNotContain("s.distance_m asc");
    }

    [Theory]
    [InlineData(SearchSort.Soonest, "order by s.next_start_at asc, s.listing_id asc")]
    [InlineData(SearchSort.Newest, "order by s.published_at desc, s.listing_id desc")]
    [InlineData(SearchSort.Relevance, "order by s.score desc, s.listing_id desc")]
    public void Each_sort_orders_by_its_key_and_a_stable_id(SearchSort sort, string expected) =>
        SearchQueryBuilder.Build(Browse(sort)).Sql.ShouldContain(expected);

    [Theory]
    [InlineData(SearchSort.Soonest, "(s.next_start_at, s.listing_id) > (@cursor_value::timestamptz, @cursor_id::uuid)")]
    [InlineData(SearchSort.Newest, "(s.published_at, s.listing_id) < (@cursor_value::timestamptz, @cursor_id::uuid)")]
    [InlineData(SearchSort.Relevance, "(s.score, s.listing_id) < (@cursor_value::numeric, @cursor_id::uuid)")]
    public void The_keyset_comparison_follows_the_sort_direction(SearchSort sort, string expected)
    {
        // Ascending sorts page with '>' and descending with '<', with the id direction
        // matching. Mismatch these and tied rows are skipped or repeated across pages.
        var cursor = new Cursor("ignored", Guid.CreateVersion7());

        BuiltQuery query = SearchQueryBuilder.Build(Browse(sort) with { After = cursor });

        query.Sql.ShouldContain(expected);
    }

    [Fact]
    public void The_first_page_has_no_cursor_predicate()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse());

        query.Sql.ShouldNotContain("cursor_value");
        query.Parameters.ContainsKey("cursor_value").ShouldBeFalse();
    }

    [Fact]
    public void The_page_size_fetches_one_more_row_to_detect_a_next_page()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with { PageSize = new PageSize(24) });

        query.Parameters["page_size"].ShouldBe(25);
        query.Sql.ShouldContain("limit @page_size");
    }

    [Fact]
    public void Facets_bind_as_arrays_so_containment_uses_the_gin_index()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with
        {
            AllFacets = ["age:15", "cat:stem", "free"],
            AnyFacets = ["lang:en", "lang:es"],
        });

        query.Sql.ShouldContain("ls.facets @> @facets_all");
        query.Sql.ShouldContain("ls.facets && @facets_any");
        query.Parameters["facets_all"].ShouldBe(new[] { "age:15", "cat:stem", "free" });
    }

    [Fact]
    public void Free_only_takes_precedence_over_a_maximum_cost()
    {
        BuiltQuery query = SearchQueryBuilder.Build(Browse() with { FreeOnly = true, MaxCostCents = 5000 });

        query.Sql.ShouldContain("and ls.is_free");
        query.Sql.ShouldNotContain("cost_cents <=");
    }

    [Fact]
    public void The_interest_overlap_is_computed_only_when_interests_are_supplied()
    {
        SearchQueryBuilder.Build(Browse()).Sql.ShouldContain("0 as interest_overlap");

        SearchQueryBuilder.Build(Browse() with { InterestTokens = ["int:robotics"] }).Sql
            .ShouldContain("count(*) from unnest(ls.facets) f where f = any(@interest_tokens)");
    }

    [Fact]
    public void The_score_weights_match_the_ranking_module()
    {
        // The SQL inlines the weights. This keeps them honest against RelevanceScorer.
        string sql = SearchQueryBuilder.Build(Browse()).Sql;

        sql.ShouldContain($"{RelevanceScorer.TextWeight.ToString(System.Globalization.CultureInfo.InvariantCulture)} * least");
        sql.ShouldContain($"{RelevanceScorer.PopularityWeight.ToString(System.Globalization.CultureInfo.InvariantCulture)} * least");
    }

    [Fact]
    public void The_tenant_predicate_is_present_whatever_filters_are_set()
    {
        // Decision D13: the finder reads without RLS, so this predicate is the isolation.
        // It must survive every combination of filters, sort and cursor.
        var everything = new SearchQuery
        {
            TenantId = Tenant,
            AsOf = At,
            Text = "robotics",
            AllFacets = ["age:15"],
            AnyFacets = ["lang:es"],
            Kinds = ["program"],
            WindowStart = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            WindowEnd = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            MaxCostCents = 5000,
            Origin = new Coordinate(41.0, -73.7),
            Radius = Distance.FromMetres(5000),
            InterestTokens = ["int:robotics"],
            Sort = SearchSort.Nearest,
            After = new Cursor("12.5", Guid.CreateVersion7()),
        };

        foreach (SearchQuery query in new[] { Browse(), everything, everything with { FreeOnly = true } })
        {
            BuiltQuery built = SearchQueryBuilder.Build(query);

            built.Sql.ShouldContain("where ls.tenant_id = @tenant_id and ls.is_published");
            built.Parameters["tenant_id"].ShouldBe(Tenant);
        }
    }
}
