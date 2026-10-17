using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The public listing page's reader, on the application login under RLS. What matters most
/// here is what it refuses to return.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ListingDetailsTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    private sealed record Seeded(Guid Tenant, Guid Listing, string Slug, Guid PublicSite, Guid PrivateSite);

    [Fact]
    public async Task A_published_listing_is_read_with_its_places_terms_and_schedule()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();

        ListingDetail? detail = await ReadAsync(s.Tenant, s.Tenant, s.Slug);

        detail.ShouldNotBeNull();
        detail.Title.ShouldBe("Robotics club");
        detail.OrganisationName.ShouldBe("Kettle Youth Collective");
        detail.Description.ShouldBe("Teams build a robot.\n\nNo experience needed.");
        detail.RegistrationMode.ShouldBe(RegistrationMode.External);
        detail.ExternalUrl.ShouldBe("https://example.org/register");
        detail.Categories.ShouldBe(["STEM"], "the localised name, not the code");
        detail.Accommodations.ShouldBe(["wheelchair-accessible"], "no name row, so the code");
        detail.Languages.ShouldBe(["es"]);
    }

    [Fact]
    public async Task A_private_address_never_leaves_Directory_and_an_inactive_site_is_left_out()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();

        ListingDetail detail = (await ReadAsync(s.Tenant, s.Tenant, s.Slug))!;

        detail.Places.Count.ShouldBe(2);

        ListingPlace open = detail.Places.Single(p => p.IsAddressPublic);
        open.AddressLine1.ShouldBe("40 Orchard Hill Road");

        ListingPlace hidden = detail.Places.Single(p => !p.IsAddressPublic);
        hidden.Name.ShouldBe("Rehearsal studio");
        hidden.NeighbourhoodName.ShouldBe("Westside");
        hidden.AddressLine1.ShouldBeNull();
        hidden.AddressLine2.ShouldBeNull();
        hidden.Locality.ShouldBeNull();
        hidden.PostalCode.ShouldBeNull();
        hidden.TransitNote.ShouldBeNull();
    }

    [Fact]
    public async Task Upcoming_is_future_uncancelled_in_order_and_capped()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();

        ListingDetail detail = (await ReadAsync(s.Tenant, s.Tenant, s.Slug))!;

        detail.Upcoming.Count.ShouldBe(SqlListingDetails.UpcomingShown, "twelve are seeded in the future");
        detail.Upcoming.ShouldAllBe(t => t.StartsAt > DateTimeOffset.UtcNow);
        detail.Upcoming.Select(t => t.StartsAt).ShouldBeInOrder(SortDirection.Ascending);
        detail.Upcoming[0].StartsAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(1.5),
            "the session tomorrow is cancelled");
        detail.Deadlines.Select(d => d.Kind).ShouldBe(["registration"], "the past deadline is gone");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("pending_review")]
    [InlineData("unpublished")]
    [InlineData("archived")]
    public async Task Anything_but_published_is_not_found(string status)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();
        await ExecAsync(
            "update opportunity.listing set status = @s, published_at = null where id = @l",
            ("s", status), ("l", s.Listing));

        (await ReadAsync(s.Tenant, s.Tenant, s.Slug)).ShouldBeNull();
    }

    [Fact]
    public async Task An_expired_listing_is_not_found()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();
        await ExecAsync(
            "update opportunity.listing set expires_at = now() - interval '1 minute' where id = @l",
            ("l", s.Listing));

        (await ReadAsync(s.Tenant, s.Tenant, s.Slug)).ShouldBeNull();
    }

    [Fact]
    public async Task Another_tenant_cannot_read_it_even_by_naming_its_tenant()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();
        Guid other = UuidV7.NewId();
        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, other, "other-" + other.ToString("N"));
        }

        // The other tenant's session, asking for this tenant's listing by its id: RLS answers.
        (await ReadAsync(sessionTenant: other, askedTenant: s.Tenant, s.Slug)).ShouldBeNull();
        (await ReadAsync(sessionTenant: other, askedTenant: other, s.Slug)).ShouldBeNull();
    }

    [Fact]
    public async Task The_slug_matches_without_regard_to_case()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();

        (await ReadAsync(s.Tenant, s.Tenant, s.Slug.ToUpperInvariant())).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("broken", false)]
    [InlineData("blocked", false)]
    [InlineData("unchecked", true)]
    [InlineData("redirect", true)]
    public async Task A_registration_link_the_checker_rejected_is_withheld(string state, bool shown)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedAsync();
        await ExecAsync(
            "update opportunity.listing set external_url_state = @s where id = @l",
            ("s", state), ("l", s.Listing));

        ListingDetail detail = (await ReadAsync(s.Tenant, s.Tenant, s.Slug))!;

        (detail.ExternalUrl is not null).ShouldBe(shown);
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<ListingDetail?> ReadAsync(Guid sessionTenant, Guid askedTenant, string slug)
    {
        await using AsyncServiceScope scope = TestHost.AppScope(Cs, sessionTenant);
        IServiceProvider sp = scope.ServiceProvider;
        var reader = new SqlListingDetails(
            sp.GetRequiredService<OpportunitiesDbContext>(),
            sp.GetRequiredService<IDirectoryQueries>(),
            sp.GetRequiredService<ITaxonomyQueries>(),
            sp.GetRequiredService<IGeographyQueries>(),
            SystemClock.Instance);

        return await reader.GetPublishedAsync(new TenantId(askedTenant), slug, Locale.English.Code, Ct);
    }

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.ExecAsync(connection, null, sql, parameters);
    }

    private async Task<Seeded> SeedAsync()
    {
        await TestHost.EnsureLoginsAsync(Cs, Ct);

        Guid tenant = UuidV7.NewId(), org = UuidV7.NewId(), hood = UuidV7.NewId();
        Guid open = UuidV7.NewId(), hidden = UuidV7.NewId(), closed = UuidV7.NewId();
        Guid stem = UuidV7.NewId(), wheelchair = UuidV7.NewId(), listing = UuidV7.NewId(), session = UuidV7.NewId();
        string slug = "robotics-" + listing.ToString("N")[..8];

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "detail-" + tenant.ToString("N"));
        await Sql.ExecAsync(connection, null,
            """
            insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind)
            values (@org, @t, 'Kettle Youth Collective Inc.', 'Kettle Youth Collective', @orgslug, 'nonprofit');
            insert into platform.neighbourhood (id, tenant_id, code, kind) values (@hood, @t, @hoodcode, 'neighbourhood');
            insert into platform.neighbourhood_name (neighbourhood_id, locale, name) values (@hood, 'en', 'Westside');

            insert into directory.site (id, tenant_id, org_id, name, address_line1, locality, neighbourhood_id, is_address_public, transit_note)
            values (@open, @t, @org, 'Westside High School', '40 Orchard Hill Road', 'Kettleton', @hood, true, 'Bus 7'),
                   (@hidden, @t, @org, 'Rehearsal studio', '18 Linden Lane', 'Kettleton', @hood, false, 'Bus 9 to Linden Lane'),
                   (@closed, @t, @org, 'Old annex', '1 Gone Street', 'Kettleton', @hood, true, null);
            update directory.site set is_active = false where id = @closed;

            insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values
              (@stem, @t, 'category', 'stem'),
              (@wc, @t, 'accommodation', 'wheelchair-accessible');
            insert into platform.taxonomy_term_name (term_id, locale, name) values (@stem, 'en', 'STEM');

            insert into opportunity.listing (id, tenant_id, org_id, kind, slug, title, summary, description,
                                             status, published_at, registration_mode, external_url)
            values (@l, @t, @org, 'program', @slug, 'Robotics club', 'Build and program robots.',
                    E'Teams build a robot.\n\nNo experience needed.',
                    'published', now(), 'external', 'https://example.org/register');

            insert into opportunity.listing_site (listing_id, site_id) values (@l, @open), (@l, @hidden), (@l, @closed);
            insert into opportunity.listing_term (listing_id, term_id, is_primary) values (@l, @stem, true);
            insert into opportunity.listing_accommodation (listing_id, term_id) values (@l, @wc);
            insert into opportunity.listing_language (listing_id, language_code) values (@l, 'es');

            insert into opportunity.listing_deadline (id, listing_id, deadline_kind, due_at)
            values (@d1, @l, 'application', now() - interval '1 day'),
                   (@d2, @l, 'registration', now() + interval '10 days');

            insert into opportunity.listing_session (id, tenant_id, listing_id, site_id, timezone, starts_on, start_time, end_time, rrule)
            values (@s, @t, @l, @open, 'America/New_York', current_date, '15:30', '17:00', 'FREQ=DAILY');

            -- One past, one cancelled tomorrow, then twelve more: the reader must show ten.
            insert into opportunity.listing_occurrence (tenant_id, listing_id, session_id, site_id, starts_at, ends_at, day_of_week, is_cancelled)
            select @t, @l, @s, @open, now() + make_interval(days => n), now() + make_interval(days => n, hours => 1),
                   extract(dow from now() + make_interval(days => n))::smallint, n = 1
            from generate_series(-1, 13) as n where n <> 0;
            """,
            [("org", org), ("t", tenant), ("orgslug", "org-" + org.ToString("N")), ("hood", hood),
             ("hoodcode", "hood-" + hood.ToString("N")), ("open", open), ("hidden", hidden), ("closed", closed),
             ("stem", stem), ("wc", wheelchair), ("l", listing), ("slug", slug), ("s", session),
             ("d1", UuidV7.NewId()), ("d2", UuidV7.NewId())]);

        return new Seeded(tenant, listing, slug, open, hidden);
    }
}
