using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The tenancy plumbing as the application uses it: a module context, registered with
/// its interceptors, connecting as a login role that is subject to row-level security.
/// Until now the interceptor had only been read, never run against a database.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class EfTenancyTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AppLogin = "bt_app_login";
    private const string AppPassword = "bt-test-only";

    [Fact]
    public async Task A_listing_saved_through_ef_is_visible_to_its_tenant_only()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenantA, Guid orgA) = await SeedTenantAndOrgAsync("efa");
        (Guid tenantB, _) = await SeedTenantAndOrgAsync("efb");
        Guid listing = UuidV7.NewId();

        await using (AsyncServiceScope scope = Scope(tenantA))
        {
            OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
            db.Listings.Add(NewListing(listing, tenantA, orgA));
            await db.SaveChangesAsync(Ct);
        }

        int seenByA = await CountListingAsync(tenantA, listing);
        int seenByB = await CountListingAsync(tenantB, listing);

        seenByA.ShouldBe(1);
        seenByB.ShouldBe(0, "row-level security must hide another tenant's listing from EF too");
    }

    [Fact]
    public async Task A_single_row_save_still_runs_with_the_tenant_set()
    {
        // EF Core skips the transaction for a one-statement save unless told otherwise. If it
        // did here, the tenant would never be set and the policy's WITH CHECK would refuse the
        // insert. AutoTransactionBehavior.Always is what makes this pass.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, Guid org) = await SeedTenantAndOrgAsync("single");

        await using AsyncServiceScope scope = Scope(tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        db.Listings.Add(NewListing(UuidV7.NewId(), tenant, org));

        int written = await db.SaveChangesAsync(Ct);

        written.ShouldBe(1);
    }

    [Fact]
    public async Task A_read_outside_a_transaction_fails_loudly_instead_of_returning_nothing()
    {
        // Without a transaction there is no tenant, and RLS would quietly return zero rows.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, _) = await SeedTenantAndOrgAsync("loud");

        await using AsyncServiceScope scope = Scope(tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => db.Listings.CountAsync(Ct));

        error.Message.ShouldContain("outside a transaction");
    }

    [Fact]
    public async Task A_tenant_does_not_outlive_its_transaction_on_a_pooled_connection()
    {
        // set_config(..., is_local => true) unwinds at commit. A connection returned to the
        // pool must not carry one request's tenant into the next.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, _) = await SeedTenantAndOrgAsync("pool");
        string connection = AppConnection();

        await using (AsyncServiceScope scope = Scope(tenant))
        {
            OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
            await db.InTransactionAsync(ct => db.Listings.CountAsync(ct), Ct);
        }

        // Same connection string, same pool: very likely the same physical connection.
        await using NpgsqlConnection reused = await Sql.OpenAsync(connection, Ct);
        string? leftOver = await Sql.ScalarAsync<string>(reused, null,
            "select coalesce(current_setting('brighttime.tenant_id', true), '')", []);

        leftOver.ShouldBeEmpty();
    }

    [Fact]
    public async Task Projected_rows_written_through_ef_are_found_by_the_sql_finder()
    {
        // The whole read path end to end: projector, writer, then the finder that families use.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, Guid org) = await SeedTenantAndOrgAsync("pipeline");
        Guid listing = UuidV7.NewId();

        await using (AsyncServiceScope scope = Scope(tenant))
        {
            OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
            var writer = new ListingSearchWriter(db, SystemClock.Instance);

            ListingRecord record = NewListing(listing, tenant, org);
            record.Title = "Marine biology club";
            record.Summary = "Study tide pools and coastal ecosystems.";

            IReadOnlyList<ListingSearchRow> rows = ListingSearchProjector.Project(new ProjectionSource
            {
                ListingId = listing,
                OrganisationId = org,
                OrganisationName = "Shoreline Science",
                IsPublished = true,
                PublishedAt = DateTimeOffset.UtcNow,
                Facets = new FacetInput { Kind = ListingKind.Program, OrganisationId = org, AgeMin = 12, AgeMax = 16 },
                Title = record.Title,
                Slug = record.Slug,
                Summary = record.Summary,
                Sites = [new SiteSource(UuidV7.NewId(), null, new Coordinate(41.03, -73.76))],
                OccurrenceStarts = [DateTimeOffset.UtcNow.AddDays(3)],
                OccurrenceEnds = [DateTimeOffset.UtcNow.AddDays(3).AddHours(1)],
                IsFree = true,
            });

            await db.InTransactionAsync(async ct =>
            {
                db.Listings.Add(record);
                await db.SaveChangesAsync(ct);
                await writer.ReplaceAsync(tenant, listing, rows, Locale.TextSearchEnglish, ct);
                return 0;
            }, Ct);
        }

        await EnsureFinderMembershipAsync();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(AppConnection());
        FinderPage page = await new SqlListingFinder(source).SearchAsync(new SearchQuery
        {
            TenantId = tenant,
            Text = "tide pools",
            TextSearchConfig = Locale.TextSearchEnglish,
            AllFacets = ["age:14"],
            PageSize = new PageSize(10),
        }, Ct);

        page.Items.Select(i => i.ListingId.Value).ShouldBe([listing]);
    }

    [Fact]
    public async Task Replacing_a_listings_rows_leaves_no_stale_row_behind()
    {
        // A site removed from a listing must disappear from search, not linger.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, Guid org) = await SeedTenantAndOrgAsync("replace");
        Guid listing = UuidV7.NewId();

        await using AsyncServiceScope scope = Scope(tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        var writer = new ListingSearchWriter(db, SystemClock.Instance);

        ProjectionSource twoSites = new()
        {
            ListingId = listing,
            OrganisationId = org,
            OrganisationName = "Org",
            IsPublished = true,
            Facets = new FacetInput { Kind = ListingKind.Program, OrganisationId = org },
            Title = "Two sites",
            Slug = "two-sites-" + listing.ToString("N"),
            Summary = "Then one.",
            Sites =
            [
                new SiteSource(UuidV7.NewId(), null, new Coordinate(41.03, -73.76)),
                new SiteSource(UuidV7.NewId(), null, new Coordinate(41.05, -73.80)),
            ],
        };

        int remaining = await db.InTransactionAsync(async ct =>
        {
            db.Listings.Add(NewListing(listing, tenant, org));
            await db.SaveChangesAsync(ct);
            await writer.ReplaceAsync(tenant, listing, ListingSearchProjector.Project(twoSites), Locale.TextSearchEnglish, ct);
            await writer.ReplaceAsync(tenant, listing, ListingSearchProjector.Project(twoSites with { Sites = [twoSites.Sites[0]] }), Locale.TextSearchEnglish, ct);
            return await db.ListingSearch.CountAsync(r => r.ListingId == listing, ct);
        }, Ct);

        remaining.ShouldBe(1);
    }

    private static ListingRecord NewListing(Guid id, Guid tenant, Guid org) => new()
    {
        Id = id,
        TenantId = tenant,
        OrgId = org,
        Kind = "program",
        Slug = "ef-" + id.ToString("N"),
        Title = "EF listing",
        Summary = "Written through the module context.",
        RegistrationMode = "external",
        ExternalUrl = "https://example.org/register",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<int> CountListingAsync(Guid tenant, Guid listing)
    {
        await using AsyncServiceScope scope = Scope(tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        return await db.InTransactionAsync(ct => db.Listings.CountAsync(l => l.Id == listing, ct), Ct);
    }

    /// <summary>A request scope as the host builds one: tenant set, context with its interceptors.</summary>
    private AsyncServiceScope Scope(Guid tenant)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddBrightTimeDbContext<OpportunitiesDbContext>(
            AppConnection(), OpportunitiesDbContext.SchemaName, OpportunitiesDbContext.MigrationsHistoryTable);

        AsyncServiceScope scope = services.BuildServiceProvider().CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(tenant), TimeZoneInfo.Utc, Locale.English);
        return scope;
    }

    private string AppConnection() =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = AppLogin,
            Password = AppPassword,
        }.ConnectionString;

    private async Task<(Guid Tenant, Guid Org)> SeedTenantAndOrgAsync(string label)
    {
        Guid tenant = UuidV7.NewId();
        Guid org = UuidV7.NewId();

        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);
        await Sql.ExecAsync(connection, null,
            """
            do $$ begin
              if not exists (select from pg_roles where rolname = 'bt_app_login') then
                create role bt_app_login login password 'bt-test-only' in role brighttime_app;
              end if;
            end $$;
            """, []);
        await Sql.SeedTenantAsync(connection, tenant, label + "-" + tenant.ToString("N"));
        await Sql.ExecAsync(connection, null,
            "insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind) " +
            "values (@o, @t, 'Org', 'Org', @s, 'nonprofit')",
            [("o", org), ("t", tenant), ("s", "org-" + org.ToString("N"))]);

        return (tenant, org);
    }

    private async Task EnsureFinderMembershipAsync()
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);
        await Sql.ExecAsync(connection, null, "grant brighttime_finder to bt_app_login", []);
    }
}
