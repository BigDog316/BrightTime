using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Paging;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// Decision D13: the public finder reads <c>opportunity.listing_search</c> as
/// <c>brighttime_finder</c>, which bypasses row-level security. These tests hold the
/// two things that must stay true once RLS is out of that path: the role reaches
/// nothing else, and the finder still never crosses tenants.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FinderRoleTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task The_finder_role_reads_the_search_table_without_tenant_context()
    {
        // The point of the role. Without a tenant setting, RLS would hide every row.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, tenant, "fr-" + tenant.ToString("N"));
            await RowLevelSecurityTests.SeedPublishedListingAsync(
                seed, tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), "Visible to the finder");
        }

        long rows = await RunAsFinderWithoutTenantAsync(c =>
            Sql.ScalarAsync<long>(c.Connection, c.Transaction,
                "select count(*) from opportunity.listing_search where tenant_id = @t", [("t", tenant)]));

        rows.ShouldBe(1);
    }

    [Theory]
    [InlineData("identity.app_user")]
    [InlineData("identity.parental_consent")]
    [InlineData("enrollment.application")]
    [InlineData("enrollment.family_member")]
    [InlineData("attendance.attendance_record")]
    [InlineData("community.message")]
    [InlineData("opportunity.listing")]
    [InlineData("platform.audit_event")]
    public async Task The_finder_role_cannot_read_any_other_table(string table)
    {
        // A role that bypasses RLS must never reach a youth record. If it could, a bug
        // anywhere on the finder path would become a cross-tenant disclosure.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            RunAsFinderWithoutTenantAsync(c =>
                Sql.ScalarAsync<long>(c.Connection, c.Transaction, $"select count(*) from {table}", [])));

        error.SqlState.ShouldBe("42501", $"brighttime_finder could read {table}");
    }

    [Theory]
    [InlineData("insert into opportunity.listing_search (id) values (gen_random_uuid())")]
    [InlineData("update opportunity.listing_search set title = title")]
    [InlineData("delete from opportunity.listing_search")]
    public async Task The_finder_role_cannot_change_the_search_table(string statement)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            RunAsFinderWithoutTenantAsync(async c =>
            {
                await Sql.ExecAsync(c.Connection, c.Transaction, statement, []);
                return 0;
            }));

        error.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task The_finder_role_holds_select_on_exactly_one_table()
    {
        // The same rule the migration asserts, checked independently of that file.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        string reachable = await Sql.ScalarAsync<string>(connection, null,
            """
            select coalesce(string_agg(format('%s.%s:%s', n.nspname, c.relname, p.priv), ',' order by 1), '')
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE')) p(priv)
            where c.relkind in ('r','p','v','m')
              and n.nspname in ('platform','identity','directory','opportunity','enrollment','attendance',
                                'learning','community','workforce','landscape','analytics','notify','integration')
              and has_table_privilege('brighttime_finder', c.oid, p.priv)
            """, []);

        reachable.ShouldBe("opportunity.listing_search:SELECT");
    }

    [Fact]
    public async Task Only_the_migrator_and_the_finder_can_bypass_row_level_security()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        string bypassers = await Sql.ScalarAsync<string>(connection, null,
            "select string_agg(rolname, ',' order by rolname) from pg_roles " +
            "where rolname like 'brighttime\\_%' and rolbypassrls", []);

        bypassers.ShouldBe("brighttime_finder,brighttime_migrator");
    }

    [Fact]
    public async Task The_finder_never_returns_another_tenants_listing()
    {
        // With RLS out of this path, the tenant predicate is the isolation. Two tenants,
        // one listing each, titled identically so only the tenant can tell them apart.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenantA = Guid.CreateVersion7();
        Guid tenantB = Guid.CreateVersion7();
        Guid listingA = Guid.CreateVersion7();
        Guid listingB = Guid.CreateVersion7();

        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, tenantA, "iso-a-" + tenantA.ToString("N"));
            await Sql.SeedTenantAsync(seed, tenantB, "iso-b-" + tenantB.ToString("N"));
            await RowLevelSecurityTests.SeedPublishedListingAsync(seed, tenantA, Guid.CreateVersion7(), listingA, "Shared title");
            await RowLevelSecurityTests.SeedPublishedListingAsync(seed, tenantB, Guid.CreateVersion7(), listingB, "Shared title");
        }

        await EnsureFinderLoginAsync();

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(FinderLoginConnection());
        var finder = new SqlListingFinder(source, BrightTime.SharedKernel.Time.SystemClock.Instance);

        FinderPage page = await finder.SearchAsync(new SearchQuery
        {
            TenantId = tenantA,
            Text = "Shared title",
            TextSearchConfig = SharedKernel.Primitives.Locale.TextSearchEnglish,
            PageSize = new PageSize(50),
        }, Ct);

        page.Items.Select(i => i.ListingId.Value).ShouldBe([listingA]);
    }

    [Fact]
    public async Task The_bypass_does_not_outlive_the_finders_transaction()
    {
        // SET LOCAL ROLE reverts at commit. A pooled connection must come back as the
        // login role, under RLS, not as the finder.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await EnsureFinderLoginAsync();

        await using NpgsqlConnection connection = await Sql.OpenAsync(FinderLoginConnection(), Ct);
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct))
        {
            await Sql.ExecAsync(connection, transaction, "set local role " + SqlListingFinder.FinderRole, []);
            (await Sql.ScalarAsync<string>(connection, transaction, "select current_user", []))
                .ShouldBe("brighttime_finder");
            await transaction.CommitAsync(Ct);
        }

        (await Sql.ScalarAsync<string>(connection, null, "select current_user", []))
            .ShouldBe("bt_finder_login");
    }

    private async Task<T> RunAsFinderWithoutTenantAsync<T>(Func<(NpgsqlConnection Connection, NpgsqlTransaction Transaction), Task<T>> body)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);

        try
        {
            await Sql.ExecAsync(connection, transaction, "set local role " + SqlListingFinder.FinderRole, []);
            return await body((connection, transaction));
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    private string FinderLoginConnection() =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = "bt_finder_login",
            Password = "bt-test-only",
        }.ConnectionString;

    private async Task EnsureFinderLoginAsync()
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.ExecAsync(connection, null,
            """
            do $$ begin
              if not exists (select from pg_roles where rolname = 'bt_finder_login') then
                create role bt_finder_login login password 'bt-test-only' in role brighttime_app;
              end if;
              grant brighttime_finder to bt_finder_login;
            end $$;
            """, []);
    }
}
