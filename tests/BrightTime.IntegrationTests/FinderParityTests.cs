using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The production finder, run against PostgreSQL, next to the in-memory finder, over
/// the same rows. Where the in-memory finder claims to match the SQL, these tests
/// hold it to that claim. Text search is compared on membership only, since the
/// in-memory matcher is approximate.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FinderParityTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string LoginRole = "bt_finder_login";
    private const string LoginPassword = "bt-test-only";

    private string SuperuserConnection => database.ConnectionString;

    /// <summary>Runs <paramref name="body"/> against a finder that connects as the login role, then disposes its data source.</summary>
    private async Task<T> WithFinderAsync<T>(Func<SqlListingFinder, Task<T>> body)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(FinderConnection());
        return await body(new SqlListingFinder(source)).ConfigureAwait(false);
    }

    /// <summary>The finder connects as a login role that inherits brighttime_app, never as a superuser.</summary>
    private string FinderConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = LoginRole,
            Password = LoginPassword,
        };
        return builder.ConnectionString;
    }

    [Theory]
    [InlineData("browse")]
    [InlineData("free")]
    [InlineData("job")]
    [InlineData("age16")]
    [InlineData("maxCost")]
    [InlineData("robotics")]
    [InlineData("window")]
    [InlineData("spanish")]
    [InlineData("combined")]
    public async Task The_sql_and_in_memory_finders_return_the_same_listings(string scenario)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await SeedSampleCatalogueAsync();
        IReadOnlyList<ListingSearchRow> rows = KettleCatalogue.Rows(SystemClock.Instance);

        SearchQuery query = Scenario(scenario);

        HashSet<Guid> sql = await WithFinderAsync(f => Membership(f, query));
        HashSet<Guid> memory = await Membership(new InMemoryListingFinder(rows, SystemClock.Instance), query);

        sql.ShouldBe(memory, $"scenario '{scenario}': the finders disagree on which listings match");
        sql.ShouldNotBeEmpty($"scenario '{scenario}' matched nothing, so it proves nothing");
    }

    [Fact]
    public async Task Soonest_is_the_same_order_in_both_finders()
    {
        // Ordering is exact (no score), so the sequence must match, not only the set.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await SeedSampleCatalogueAsync();
        IReadOnlyList<ListingSearchRow> rows = KettleCatalogue.Rows(SystemClock.Instance);
        SearchQuery query = Scenario("browse") with { Sort = SearchSort.Soonest, PageSize = new PageSize(50) };

        List<Guid> sql = await WithFinderAsync(f => Order(f, query));
        List<Guid> memory = await Order(new InMemoryListingFinder(rows, SystemClock.Instance), query);

        sql.ShouldBe(memory);
    }

    [Theory]
    [InlineData(SearchSort.Relevance)]
    [InlineData(SearchSort.Soonest)]
    [InlineData(SearchSort.Newest)]
    public async Task Walking_every_page_against_the_database_returns_each_listing_exactly_once(SearchSort sort)
    {
        // Keyset paging is the thing most likely to be subtly wrong in SQL: a comparison
        // in the wrong direction skips or repeats rows at a page boundary. Small pages
        // put a boundary everywhere.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await SeedSampleCatalogueAsync();

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(FinderConnection());
        var finder = new SqlListingFinder(source);

        List<Guid> everything = await Order(finder, Scenario("browse") with { Sort = sort, PageSize = new PageSize(50) });

        List<Guid> walked = [];
        string? cursor = null;
        int pages = 0;

        do
        {
            SearchQuery query = Scenario("browse") with
            {
                Sort = sort,
                PageSize = new PageSize(2),
                After = cursor is null ? null : Cursor.TryDecode(cursor, out Cursor c) ? c : null,
            };

            FinderPage page = await finder.SearchAsync(query, Ct);
            walked.AddRange(page.Items.Select(i => i.ListingId.Value));
            cursor = page.NextCursor;
            pages++;
            pages.ShouldBeLessThan(20, "pagination must terminate");
        }
        while (cursor is not null);

        walked.Count.ShouldBe(walked.Distinct().Count(), "a listing appeared on two pages");
        walked.Count.ShouldBe(everything.Count, "a listing was skipped at a page boundary");
        walked.ToHashSet().ShouldBe(everything.ToHashSet());
        pages.ShouldBeGreaterThan(1, "the test must actually cross a page boundary");
    }

    [Fact]
    public async Task The_database_never_returns_a_listing_that_is_not_published()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await SeedSampleCatalogueAsync();

        // A draft that has a search row carrying is_published = false. The schema keeps
        // it in the table for the projector to manage; the finder must still hide it.
        Guid draft = Guid.CreateVersion7();
        Guid org = KettleCatalogue.Organisations[0];
        await using (NpgsqlConnection connection = await Sql.OpenAsync(SuperuserConnection, Ct))
        {
            await Sql.ExecAsync(connection, null,
                """
                insert into opportunity.listing
                  (id, tenant_id, org_id, kind, slug, title, summary, status, registration_mode, cost_model, modality)
                values (@id, @tenant, @org, 'program', @slug, 'Unpublished draft', 'Not for families yet.',
                        'draft', 'none', 'free', 'in_person')
                """,
                [("id", draft), ("tenant", KettleCatalogue.TenantId), ("org", org), ("slug", "draft-" + draft.ToString("N"))]);
            await Sql.ExecAsync(connection, null,
                """
                insert into opportunity.listing_search
                  (id, tenant_id, listing_id, site_id, org_id, kind, is_published, facets, cost_cents, is_free,
                   modality, title, summary, fuzzy_text, search_vector)
                values (@row, @tenant, @listing, null, @org, 'program', false, array['kind:program'], 0, true,
                        'in_person', 'Unpublished draft', 'Not for families yet.', 'Unpublished draft',
                        to_tsvector('platform.ts_en'::regconfig, 'Unpublished draft'))
                """,
                [("row", Guid.CreateVersion7()), ("tenant", KettleCatalogue.TenantId), ("listing", draft), ("org", org)]);
        }

        FinderPage page = await WithFinderAsync(f => f.SearchAsync(
            Scenario("browse") with { Text = "Unpublished", PageSize = new PageSize(50) }, Ct));

        page.Items.ShouldBeEmpty();
    }

    private static SearchQuery Scenario(string name)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // English text search, as the web layer sets it for an English request. The
        // default configuration is 'simple', which does not stem, and the two would
        // disagree for reasons that have nothing to do with the finder.
        SearchQuery basic = new()
        {
            TenantId = KettleCatalogue.TenantId,
            PageSize = new PageSize(50),
            TextSearchConfig = SharedKernel.Primitives.Locale.TextSearchEnglish,
        };

        return name switch
        {
            "browse" => basic,
            "free" => basic with { FreeOnly = true },
            "job" => basic with { Kinds = ["job"] },
            "age16" => basic with { AllFacets = ["age:16"] },
            "maxCost" => basic with { MaxCostCents = 5_000 },
            "robotics" => basic with { Text = "robotics" },
            "window" => basic with { WindowStart = now.AddDays(7), WindowEnd = now.AddDays(14) },
            "spanish" => basic with { AnyFacets = ["lang:es"] },
            "combined" => basic with { Kinds = ["program"], FreeOnly = true, AllFacets = ["age:12"] },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown scenario."),
        };
    }

    private static async Task<HashSet<Guid>> Membership(IListingFinder finder, SearchQuery query) =>
        (await finder.SearchAsync(query, Ct)).Items.Select(i => i.ListingId.Value).ToHashSet();

    private static async Task<List<Guid>> Order(IListingFinder finder, SearchQuery query) =>
        (await finder.SearchAsync(query, Ct)).Items.Select(i => i.ListingId.Value).ToList();

    /// <summary>
    /// Replaces the sample tenant's rows with the current sample, so every test sees the
    /// same data. Tests in this collection run one at a time, so this cannot race.
    /// </summary>
    private async Task SeedSampleCatalogueAsync()
    {
        IReadOnlyList<ListingSearchRow> rows = KettleCatalogue.Rows(SystemClock.Instance);
        Guid tenant = KettleCatalogue.TenantId;

        await using NpgsqlConnection connection = await Sql.OpenAsync(SuperuserConnection, Ct);

        await Sql.ExecAsync(connection, null,
            """
            do $$ begin
              if not exists (select from pg_roles where rolname = 'bt_finder_login') then
                create role bt_finder_login login password 'bt-test-only' in role brighttime_app;
              end if;
              -- Production grants the application's login membership of the finder
              -- role, so the finder can SET LOCAL ROLE into it (decision D13).
              grant brighttime_finder to bt_finder_login;
            end $$;
            """, []);

        // Listings are deleted and re-created. Organisations and the tenant are not:
        // an organisation's vetting history is append-only, and a cascade from a parent
        // delete into it is refused by design. Parents are upserted, so seeding is repeatable.
        await Sql.ExecAsync(connection, null, "delete from opportunity.listing where tenant_id = @t", [("t", tenant)]);

        await Sql.SeedTenantAsync(connection, tenant, "kettle-sample");

        foreach (Guid org in rows.Select(r => r.OrganisationId).Distinct())
        {
            await Sql.ExecAsync(connection, null,
                "insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind) " +
                "values (@o, @t, 'Sample org', 'Sample org', @s, 'nonprofit') on conflict (id) do nothing",
                [("o", org), ("t", tenant), ("s", "org-" + org.ToString("N"))]);
        }

        foreach (ListingSearchRow first in rows.GroupBy(r => r.ListingId).Select(g => g.First()))
        {
            await InsertListingAsync(connection, tenant, first);
        }

        foreach (ListingSearchRow row in rows)
        {
            await InsertSearchRowAsync(connection, tenant, row);
        }
    }

    private static async Task InsertListingAsync(NpgsqlConnection connection, Guid tenant, ListingSearchRow row)
    {
        bool resource = row.Kind == "resource";
        bool job = row.Kind == "job";

        // Cost model follows the schema's rules: free has no price, a job is paid by stipend,
        // anything else with a price is a fixed price.
        string costModel = row.IsFree ? "free" : job ? "stipend_paid" : "fixed";
        int? costMin = !row.IsFree && !job ? row.CostCents : null;

        await Sql.ExecAsync(connection, null,
            """
            insert into opportunity.listing
              (id, tenant_id, org_id, kind, slug, title, summary, status, published_at,
               registration_mode, external_url, cost_model, cost_cents_min,
               modality, is_drop_in, age_min, age_max)
            values (@id, @tenant, @org, @kind, @slug, @title, @summary, 'published', @published,
                    @mode, @url, @cost, @costmin, @modality, @dropin, @agemin, @agemax)
            """,
            [
                ("id", row.ListingId), ("tenant", tenant), ("org", row.OrganisationId), ("kind", row.Kind),
                ("slug", "l-" + row.ListingId.ToString("N")), ("title", row.Title), ("summary", row.Summary),
                ("published", row.PublishedAt), ("mode", resource ? "none" : "external"),
                ("url", resource ? null : "https://example.org/" + row.ListingId.ToString("N")),
                ("cost", costModel), ("costmin", costMin), ("modality", row.Modality), ("dropin", row.IsDropIn),
                ("agemin", row.AgeMin), ("agemax", row.AgeMax),
            ]).ConfigureAwait(false);
    }

    private static async Task InsertSearchRowAsync(NpgsqlConnection connection, Guid tenant, ListingSearchRow row)
    {
        await Sql.ExecAsync(connection, null,
            """
            insert into opportunity.listing_search
              (id, tenant_id, listing_id, site_id, org_id, kind, is_published, facets,
               age_range, cost_cents, is_free, modality, neighbourhood_id, location,
               session_window, next_start_at, next_deadline_at, popularity, published_at,
               is_drop_in, title, summary, fuzzy_text, search_vector)
            values
              (@row, @tenant, @listing, @site, @org, @kind, @published, @facets,
               case when @amin::int is null then null else int4range(@amin::int, @amax::int, '[]') end,
               @cost, @free, @modality, @hood,
               case when @lon::float8 is null then null
                    else st_setsrid(st_makepoint(@lon::float8, @lat::float8), 4326)::geography end,
               case when @ws::timestamptz is null then null else tstzrange(@ws::timestamptz, @we::timestamptz) end,
               @next::timestamptz, @deadline::timestamptz, @pop::real, @pubat::timestamptz,
               @dropin, @title, @summary, @fuzzy,
               to_tsvector('platform.ts_en'::regconfig, @title || ' ' || @summary))
            """,
            [
                ("row", Guid.CreateVersion7()), ("tenant", tenant), ("listing", row.ListingId), ("site", row.SiteId),
                ("org", row.OrganisationId), ("kind", row.Kind), ("published", row.IsPublished),
                ("facets", row.Facets.ToArray()),
                ("amin", row.AgeMin), ("amax", row.AgeMax), ("cost", row.CostCents), ("free", row.IsFree),
                ("modality", row.Modality), ("hood", row.NeighbourhoodId),
                ("lon", row.Location?.Longitude), ("lat", row.Location?.Latitude),
                ("ws", row.SessionWindowStart), ("we", row.SessionWindowEnd),
                ("next", row.NextStartAt), ("deadline", row.NextDeadlineAt),
                ("pop", (float)row.Popularity), ("pubat", row.PublishedAt), ("dropin", row.IsDropIn),
                ("title", row.Title), ("summary", row.Summary), ("fuzzy", row.FuzzyText),
            ]).ConfigureAwait(false);
    }
}
