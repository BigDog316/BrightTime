using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// Tenant isolation and the youth-record actor guard, enforced by PostgreSQL.
/// Every assertion runs as the application role, never as the superuser.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RowLevelSecurityTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task A_request_sees_only_its_own_tenants_rows()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid a = Guid.CreateVersion7();
        Guid b = Guid.CreateVersion7();

        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, a, "rls-a-" + a.ToString("N"));
            await Sql.SeedTenantAsync(seed, b, "rls-b-" + b.ToString("N"));
        }

        (long count, Guid onlyId) = await Sql.AsAppAsync<(long Count, Guid OnlyId)>(Cs, a, null, async (c, t) =>
        {
            long n = await Sql.ScalarAsync<long>(c, t, "select count(*) from platform.tenant", []);
            Guid id = await Sql.ScalarAsync<Guid>(c, t, "select id from platform.tenant", []);
            return (n, id);
        }, Ct);

        count.ShouldBe(1);
        onlyId.ShouldBe(a);
    }

    [Fact]
    public async Task A_request_cannot_write_a_row_into_another_tenant()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid a = Guid.CreateVersion7();
        Guid b = Guid.CreateVersion7();

        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, a, "w-a-" + a.ToString("N"));
            await Sql.SeedTenantAsync(seed, b, "w-b-" + b.ToString("N"));
        }

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            Sql.AsAppAsync(Cs, a, null, (c, t) => Sql.ExecAsync(c, t,
                "insert into platform.tenant_locale (tenant_id, locale, is_default, is_rtl, ts_config, sort_order) " +
                "values (@b, 'en', false, false, 'platform.ts_simple', 0)",
                [("b", b)]), Ct));

        // 42501 is insufficient_privilege, which is what a failed WITH CHECK raises.
        error.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task An_unauthenticated_request_cannot_read_any_youth_account()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        Guid youth = Guid.CreateVersion7();

        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, tenant, "guard-" + tenant.ToString("N"));
            await Sql.SeedUserAsync(seed, null, youth, tenant, "youth");
        }

        long withoutActor = await Sql.AsAppAsync(Cs, tenant, null, (c, t) =>
            Sql.ScalarAsync<long>(c, t, "select count(*) from identity.app_user", []), Ct);

        // The restrictive policy hides the row, even though it is in the right tenant.
        withoutActor.ShouldBe(0);
    }

    [Fact]
    public async Task The_same_request_with_an_actor_sees_the_account()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        Guid youth = Guid.CreateVersion7();

        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, tenant, "actor-" + tenant.ToString("N"));
            await Sql.SeedUserAsync(seed, null, youth, tenant, "youth");
        }

        long withActor = await Sql.AsAppAsync(Cs, tenant, youth, (c, t) =>
            Sql.ScalarAsync<long>(c, t, "select count(*) from identity.app_user", []), Ct);

        withActor.ShouldBe(1);
    }

    [Fact]
    public async Task Published_listings_are_findable_without_an_account()
    {
        // The public finder must work for an anonymous visitor. listing_search is
        // tenant-scoped only; the actor guard applies to youth records, not to this.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        Guid org = Guid.CreateVersion7();
        Guid listing = Guid.CreateVersion7();

        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, tenant, "public-" + tenant.ToString("N"));
            await SeedPublishedListingAsync(seed, tenant, org, listing, "Public listing");
        }

        long visible = await Sql.AsAppAsync(Cs, tenant, null, (c, t) =>
            Sql.ScalarAsync<long>(c, t, "select count(*) from opportunity.listing_search where is_published", []), Ct);

        visible.ShouldBe(1);
    }

    [Fact]
    public async Task The_authentication_role_cannot_read_enrolment_data()
    {
        // brighttime_auth is exempt from the actor guard so login can work. It must not
        // gain a route to youth records through that exemption, so it has no grant there.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        await using (NpgsqlConnection seed = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(seed, tenant, "auth-" + tenant.ToString("N"));
        }

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            Sql.AsRoleAsync(Cs, "brighttime_auth", tenant, null, (c, t) =>
                Sql.ScalarAsync<long>(c, t, "select count(*) from enrollment.application", []), Ct));

        error.SqlState.ShouldBe("42501");
    }

    internal static async Task SeedPublishedListingAsync(
        NpgsqlConnection connection,
        Guid tenant,
        Guid org,
        Guid listing,
        string title)
    {
        await Sql.ExecAsync(connection, null,
            """
            insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind)
            values (@org, @tenant, 'Test org', 'Test org', @orgslug, 'nonprofit')
            """,
            [("org", org), ("tenant", tenant), ("orgslug", "org-" + org.ToString("N"))]).ConfigureAwait(false);

        await Sql.ExecAsync(connection, null,
            """
            insert into opportunity.listing
              (id, tenant_id, org_id, kind, slug, title, summary, status, published_at,
               registration_mode, external_url, cost_model, modality)
            values (@id, @tenant, @org, 'program', @slug, @title, 'A test listing.', 'published', now(),
                    'external', 'https://example.org/register', 'free', 'in_person')
            """,
            [("id", listing), ("tenant", tenant), ("org", org), ("slug", "l-" + listing.ToString("N")), ("title", title)])
            .ConfigureAwait(false);

        await Sql.ExecAsync(connection, null,
            """
            insert into opportunity.listing_search
              (id, tenant_id, listing_id, site_id, org_id, kind, is_published, facets, cost_cents, is_free,
               modality, title, summary, fuzzy_text, search_vector)
            values (@row, @tenant, @listing, null, @org, 'program', true, array['kind:program','free'], 0, true,
                    'in_person', @title, 'A test listing.', @title,
                    to_tsvector('platform.ts_en'::regconfig, @title))
            """,
            [("row", Guid.CreateVersion7()), ("tenant", tenant), ("listing", listing), ("org", org), ("title", title)])
            .ConfigureAwait(false);
    }
}
