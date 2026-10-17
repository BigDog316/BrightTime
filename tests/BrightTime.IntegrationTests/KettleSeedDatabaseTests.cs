using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The seed end to end, as a deployment runs it: a database built from <c>db/schema</c> and
/// nothing else, the seed written and projected, then written again.
/// </summary>
/// <remarks>
/// It gets its own database. The shared test database already holds sample rows in the Kettle
/// tenant (the finder parity tests), and the seed's 420 listings would change their answers.
/// The database is dropped and rebuilt at the start of each run, not at the end, so a failure
/// leaves it behind to inspect.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class KettleSeedDatabaseTests(DatabaseFixture database)
{
    private const string DatabaseName = "brighttime_seedtest";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Seeded? _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(string ConnectionString, SeedData Data, SeedCounts First, SeedCounts Second);

    [Fact]
    public async Task A_fresh_database_takes_the_whole_seed_and_a_rerun_adds_nothing()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedOnceAsync();

        s.First.AddedTo("opportunity.listing").ShouldBe(420);
        s.First.AddedTo("directory.organisation").ShouldBe(60);
        s.First.AddedTo("directory.site").ShouldBe(85);
        s.First.AddedTo("platform.school").ShouldBe(28);
        s.Second.Added.ShouldBe(0, "every insert is on conflict do nothing");
    }

    [Fact]
    public async Task Every_published_listing_is_projected_with_an_upcoming_session()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedOnceAsync();
        int published = s.Data.Listings.Count(l => l.Status == "published");

        await using NpgsqlConnection connection = await Sql.OpenAsync(s.ConnectionString, Ct);
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(distinct listing_id) from opportunity.listing_search where is_published", [])).ShouldBe(published);
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from opportunity.listing_search where is_published and next_start_at is null", [])).ShouldBe(0);
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from opportunity.listing_search where not is_published", [])).ShouldBe(420 - published);
    }

    [Fact]
    public async Task Derived_boundaries_are_valid_and_contain_their_sites()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedOnceAsync();

        await using NpgsqlConnection connection = await Sql.OpenAsync(s.ConnectionString, Ct);
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from platform.neighbourhood where boundary is null or not st_isvalid(boundary::geometry)", []))
            .ShouldBe(0, "the county is a box; every other boundary is a union PostGIS computed");
        (await Sql.ScalarAsync<long>(connection, null,
            """
            select count(*) from directory.site s
            join platform.neighbourhood n on n.id = s.neighbourhood_id
            join platform.neighbourhood m on m.id = n.parent_id
            where not st_covers(m.boundary, s.location)
            """, [])).ShouldBe(0, "every site is inside its municipality");
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from platform.school_district where boundary is null", [])).ShouldBe(0);
    }

    [Fact]
    public async Task The_finder_searches_the_seed_through_its_own_role()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedOnceAsync();

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(TestHost.As(s.ConnectionString, TestHost.FinderLogin));
        var finder = new SqlListingFinder(source);

        // Text matches the title by full-text search and the organisation name by trigram, so
        // "Alder Crossing Robotics Boosters" listings match too. A title match must rank first.
        FinderPage robotics = await finder.SearchAsync(Query("robotics", null), Ct);
        string[] clubs = [.. s.Data.Listings.Where(l => l.Status == "published" && l.Title == "Robotics club").Select(l => l.Slug)];
        clubs.ShouldNotBeEmpty();

        List<string> slugs = [.. robotics.Items.Select(i => i.Slug)];
        slugs.ShouldBeSubsetOf(s.Data.Listings.Select(l => l.Slug));
        clubs.ShouldBeSubsetOf(slugs);
        slugs.Take(clubs.Length).ShouldBe(clubs, ignoreOrder: true);

        FinderPage jobs = await finder.SearchAsync(Query(null, ["kind:job", "age:17"]), Ct);
        jobs.Items.ShouldNotBeEmpty();
        jobs.Items.ShouldAllBe(i => i.Kind == ListingKind.Job);
    }

    [Fact]
    public async Task The_listing_page_reads_a_translation_and_hides_drafts()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedOnceAsync();

        SeedListing sixLocale = s.Data.Listings.First(l =>
            l.Status == "published" && l.PrimaryLanguage == "en" && l.Translations.Any(t => t.Locale == "ar"));
        SeedListing draft = s.Data.Listings.First(l => l.Status == "draft");

        ListingDetail? arabic = await ReadAsync(s.ConnectionString, sixLocale.Slug, "ar");
        arabic.ShouldNotBeNull();
        arabic.Title.ShouldBe(sixLocale.Translations.Single(t => t.Locale == "ar").Title);
        arabic.ContentLanguage.ShouldBe("ar");
        arabic.IsMachineTranslated.ShouldBeTrue("the seed's translations are unreviewed");
        arabic.DescriptionLanguage.ShouldBe("en", "no Arabic description was written, so the original is shown");

        ListingDetail english = (await ReadAsync(s.ConnectionString, sixLocale.Slug, "en"))!;
        english.Title.ShouldBe(sixLocale.Title);
        english.IsMachineTranslated.ShouldBeFalse();

        (await ReadAsync(s.ConnectionString, draft.Slug, "en")).ShouldBeNull();
    }

    // ---- helpers ----------------------------------------------------------------------

    private static SearchQuery Query(string? text, IReadOnlyList<string>? facets) => new()
    {
        TenantId = KettleSeed.TenantId,
        Text = text,
        TextSearchConfig = Locale.TextSearchEnglish,
        AllFacets = facets,
        PageSize = new PageSize(24),
    };

    private static async Task<ListingDetail?> ReadAsync(string connection, string slug, string locale)
    {
        await using AsyncServiceScope scope = TestHost.AppScope(connection, KettleSeed.TenantId);
        IServiceProvider sp = scope.ServiceProvider;
        var reader = new SqlListingDetails(
            sp.GetRequiredService<OpportunitiesDbContext>(),
            sp.GetRequiredService<IDirectoryQueries>(),
            sp.GetRequiredService<ITaxonomyQueries>(),
            sp.GetRequiredService<IGeographyQueries>(),
            SystemClock.Instance);

        return await reader.GetPublishedAsync(new TenantId(KettleSeed.TenantId), slug, locale, Ct);
    }

    private async Task<Seeded> SeedOnceAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            return _seeded ??= await BuildAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The repository's own DDL files and a constant database name.")]
    private async Task<Seeded> BuildAsync()
    {
        await TestHost.EnsureLoginsAsync(database.ConnectionString, Ct);

        await using (NpgsqlConnection admin = await Sql.OpenAsync(database.ConnectionString, Ct))
        {
            await Sql.ExecAsync(admin, null, $"drop database if exists {DatabaseName} with (force)", []);
            await Sql.ExecAsync(admin, null, $"create database {DatabaseName}", []);
        }

        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = DatabaseName }.ConnectionString;

        await using (NpgsqlConnection fresh = await Sql.OpenAsync(connection, Ct))
        {
            string schema = Path.Combine(DatabaseFixture.RepositoryRoot, "db", "schema");
            foreach (string file in Directory.GetFiles(schema, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
            {
                await using NpgsqlCommand command = new(await File.ReadAllTextAsync(file, Ct), fresh);
                await command.ExecuteNonQueryAsync(Ct);
            }
        }

        SeedData data = KettleSeed.Build(SystemClock.Instance.TodayIn(TimeZoneInfo.FindSystemTimeZoneById(KettleListings.Zone)));

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connection);
        var writer = new SeedWriter(source);
        SeedCounts first = await writer.WriteAsync(data, Ct);
        await SeedProjection.ProjectAsync(connection, data.TenantId, [.. data.Listings.Select(l => l.Id)], NullLogger.Instance, Ct);
        SeedCounts second = await writer.WriteAsync(data, Ct);

        return new Seeded(connection, data, first, second);
    }
}
