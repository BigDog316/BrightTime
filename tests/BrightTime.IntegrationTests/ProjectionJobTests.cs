using BrightTime.Infrastructure.Jobs;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The projection job end to end: a listing written by the application, a projection
/// scheduled in the same transaction, the worker running it, and the finder seeing the result.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ProjectionJobTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    private sealed record Seeded(Guid Tenant, Guid Org, Guid Site, Guid Hood, Guid Listing, Guid Session, string Slug);

    [Fact]
    public async Task A_published_listing_becomes_findable_with_its_facets_and_schedule()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);

        await DrainAsync();

        // Occurrences: every one in the future, every one on a Friday in New York.
        (long count, long fridays, long past) = await OccurrenceStatsAsync(s.Listing, dayOfWeek: 5);
        count.ShouldBeGreaterThan(20, "about 26 Fridays in a 180-day horizon");
        fridays.ShouldBe(count);
        past.ShouldBe(0);

        // The search row carries the facets the finder filters on.
        string[] facets = await FacetsAsync(s.Listing);
        facets.ShouldContain("cat:stem");
        facets.ShouldContain("accom:wheelchair-accessible");
        facets.ShouldContain("lang:es");
        facets.ShouldContain("dow:5");
        facets.ShouldContain($"hood:{s.Hood:D}");
        facets.ShouldContain("age:13");
        facets.ShouldNotContain("age:15", "the listing is for ages 12 to 14");

        // And a family finds it.
        FinderPage page = await SearchAsync(s.Tenant, "robotics", ["age:13", "cat:stem"]);
        page.Items.Select(i => i.ListingId.Value).ShouldBe([s.Listing]);
        page.Items[0].Slug.ShouldBe(s.Slug);
        page.Items[0].NextStartAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_next_start_is_the_first_future_occurrence()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Wednesday);

        await DrainAsync();

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        bool matches = await Sql.ScalarAsync<bool>(connection, null,
            """
            select ls.next_start_at = (select min(o.starts_at) from opportunity.listing_occurrence o
                                       where o.listing_id = @l and o.starts_at >= now())
            from opportunity.listing_search ls where ls.listing_id = @l
            """, [("l", s.Listing)]);
        matches.ShouldBeTrue();
    }

    [Fact]
    public async Task Unpublishing_and_reprojecting_removes_the_listing_from_search()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);
        await DrainAsync();
        (await SearchAsync(s.Tenant, "robotics", null)).Items.ShouldNotBeEmpty();

        await ChangeAndScheduleAsync(s, listing =>
        {
            listing.Status = ListingStatus.Unpublished.ToDbValue();
            listing.PublishedAt = null;
            listing.UnpublishedAt = DateTimeOffset.UtcNow;
        });
        await DrainAsync();

        (await SearchAsync(s.Tenant, "robotics", null)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moving_the_session_to_another_day_replaces_the_future_occurrences()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);
        await DrainAsync();

        await using (AsyncServiceScope scope = TestHost.AppScope(Cs, s.Tenant))
        {
            OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                ListingSessionRecord session = await db.Sessions.SingleAsync(x => x.Id == s.Session, ct);
                session.Rrule = "FREQ=WEEKLY;BYDAY=MO";
                await db.SaveChangesAsync(ct);
                await db.ScheduleProjectionAsync(new TenantId(s.Tenant), s.Listing, ct);
                return 0;
            }, Ct);
        }

        await DrainAsync();

        (long count, long mondays, _) = await OccurrenceStatsAsync(s.Listing, dayOfWeek: 1);
        mondays.ShouldBe(count, "no Friday occurrence may survive in the future");
        string[] facets = await FacetsAsync(s.Listing);
        facets.ShouldContain("dow:1");
        facets.ShouldNotContain("dow:5");
    }

    [Fact]
    public async Task Scheduling_twice_before_the_worker_runs_projects_once()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);

        bool again = await ScheduleAsync(s);

        again.ShouldBeFalse("the first request is still waiting");
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        (await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from platform.job where unique_key = @k",
            [("k", $"{ProjectListingHandler.JobType}:{s.Listing:N}")])).ShouldBe(1);
    }

    [Fact]
    public async Task A_listing_set_to_show_later_stays_hidden_and_its_projection_waits_for_the_moment()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);
        DateTimeOffset showAt = DateTimeOffset.UtcNow.AddHours(1);
        await ChangeAndScheduleAsync(s, l => l.PublishAt = showAt);

        await DrainAsync();

        (await IsPublishedAsync(s.Listing)).ShouldBeFalse("approved, but not yet shown");
        string key = $"{ProjectListingHandler.JobType}:{s.Listing:N}:at:{showAt.ToUnixTimeSeconds()}";
        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            (await Sql.ScalarAsync<bool>(connection, null,
                "select state = 'ready' and abs(extract(epoch from run_after - @at)) < 2 from platform.job where unique_key = @k",
                [("k", key), ("at", showAt.AddSeconds(1))])).ShouldBeTrue();

            // The moment comes: the listing's time has passed, and so has the job's.
            await Sql.ExecAsync(connection, null,
                """
                update opportunity.listing set publish_at = now() - interval '1 minute' where id = @l;
                update platform.job set run_after = now() where unique_key = @k;
                """, [("l", s.Listing), ("k", key)]);
        }

        await DrainAsync();

        (await IsPublishedAsync(s.Listing)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_listing_leaves_search_when_its_take_down_moment_comes()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);
        DateTimeOffset downAt = DateTimeOffset.UtcNow.AddHours(2);
        await ChangeAndScheduleAsync(s, l => l.ExpiresAt = downAt);
        await DrainAsync();
        (await IsPublishedAsync(s.Listing)).ShouldBeTrue();

        string key = $"{ProjectListingHandler.JobType}:{s.Listing:N}:at:{downAt.ToUnixTimeSeconds()}";
        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.ExecAsync(connection, null,
                """
                update opportunity.listing set expires_at = now() - interval '1 minute' where id = @l;
                update platform.job set run_after = now() where unique_key = @k;
                """, [("l", s.Listing), ("k", key)]);
        }

        await DrainAsync();

        // Before, nothing re-projected an expired listing, and it stayed findable.
        (await IsPublishedAsync(s.Listing)).ShouldBeFalse();
        (await SearchAsync(s.Tenant, "robotics", null)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_a_site_reprojects_the_listings_held_there()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Seeded s = await SeedListingAsync(DayOfWeek.Friday);
        await DrainAsync();
        (await SiteRowsAsync(s)).ShouldBe(1);

        await using (AsyncServiceScope scope = TestHost.AppScope(Cs, s.Tenant))
        {
            DirectoryDbContext db = scope.ServiceProvider.GetRequiredService<DirectoryDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                await db.Database.ExecuteSqlAsync($"update directory.site set is_active = false where id = {s.Site}", ct);
                return await db.EnqueueJobAsync(new JobRequest
                {
                    JobType = SiteChangedJob.JobType,
                    TenantId = new TenantId(s.Tenant),
                    Payload = new SiteChangedJob.Payload(s.Site),
                    UniqueKey = SiteChangedJob.UniqueKey(s.Site),
                }, ct);
            }, Ct);
        }

        await DrainAsync();

        (await SiteRowsAsync(s)).ShouldBe(0, "a closed site is no longer where the listing is found");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task DrainAsync()
    {
        await using ServiceProvider worker = TestHost.Worker(Cs, "default");
        await worker.GetRequiredService<JobRunner>().DrainAsync(Ct);

        // Every projection the tests scheduled must have succeeded; a failure would otherwise
        // only show up as a missing search row.
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        string failures = await Sql.ScalarAsync<string>(connection, null,
            "select coalesce(string_agg(last_error, ' | '), '') from platform.job " +
            "where job_type = @t and state in ('ready','dead') and last_error is not null",
            [("t", ProjectListingHandler.JobType)]);
        failures.ShouldBeEmpty();
    }

    private async Task<Seeded> SeedListingAsync(DayOfWeek day)
    {
        await TestHost.EnsureLoginsAsync(Cs, Ct);

        Guid tenant = UuidV7.NewId(), org = UuidV7.NewId(), site = UuidV7.NewId(), hood = UuidV7.NewId();
        Guid stem = UuidV7.NewId(), wheelchair = UuidV7.NewId(), listing = UuidV7.NewId(), session = UuidV7.NewId();
        string slug = "robotics-" + listing.ToString("N")[..8];

        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "proj-" + tenant.ToString("N"));
            await Sql.ExecAsync(connection, null,
                """
                insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind)
                values (@org, @t, 'Kettle Youth Collective', 'Kettle Youth Collective', @orgslug, 'nonprofit');
                insert into platform.neighbourhood (id, tenant_id, code, kind) values (@hood, @t, @hoodcode, 'neighbourhood');
                insert into directory.site (id, tenant_id, org_id, name, location, neighbourhood_id, is_address_public)
                values (@site, @t, @org, 'Kettle High School', st_setsrid(st_makepoint(-73.7629, 41.0339), 4326)::geography, @hood, true);
                insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values
                  (@stem, @t, 'category', 'stem'),
                  (@wc, @t, 'accommodation', 'wheelchair-accessible');
                insert into platform.taxonomy_term_name (term_id, locale, name) values (@stem, 'en', 'STEM');
                """,
                [("org", org), ("t", tenant), ("orgslug", "org-" + org.ToString("N")), ("hood", hood),
                 ("hoodcode", "hood-" + hood.ToString("N")), ("site", site), ("stem", stem), ("wc", wheelchair)]);
        }

        await using AsyncServiceScope scope = TestHost.AppScope(Cs, tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();

        await db.InTransactionAsync(async ct =>
        {
            db.Listings.Add(new ListingRecord
            {
                Id = listing,
                TenantId = tenant,
                OrgId = org,
                Kind = "program",
                Slug = slug,
                Title = "Robotics club",
                Summary = "Build and program robots with a team.",
                Status = "published",
                PublishedAt = DateTimeOffset.UtcNow,
                AgeMin = 12,
                AgeMax = 14,
                RegistrationMode = "external",
                ExternalUrl = "https://example.org/register",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.ListingSites.Add(new ListingSiteRecord { ListingId = listing, SiteId = site });
            db.ListingTerms.Add(new ListingTermRecord { ListingId = listing, TermId = stem, IsPrimary = true });
            db.ListingAccommodations.Add(new ListingAccommodationRecord { ListingId = listing, TermId = wheelchair });
            db.ListingLanguages.Add(new ListingLanguageRecord { ListingId = listing, LanguageCode = "es" });
            db.Sessions.Add(new ListingSessionRecord
            {
                Id = session,
                TenantId = tenant,
                ListingId = listing,
                SiteId = site,
                Timezone = "America/New_York",
                StartsOn = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-7),
                StartTime = new TimeOnly(15, 30),
                EndTime = new TimeOnly(17, 0),
                Rrule = "FREQ=WEEKLY;BYDAY=" + day.ToString()[..2].ToUpperInvariant(),
            });
            await db.SaveChangesAsync(ct);

            // In the same transaction as the change: the job exists only if the listing does.
            await db.ScheduleProjectionAsync(new TenantId(tenant), listing, ct);
            return 0;
        }, Ct);

        return new Seeded(tenant, org, site, hood, listing, session, slug);
    }

    private async Task<bool> ScheduleAsync(Seeded s)
    {
        await using AsyncServiceScope scope = TestHost.AppScope(Cs, s.Tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        return await db.InTransactionAsync(ct => db.ScheduleProjectionAsync(new TenantId(s.Tenant), s.Listing, ct), Ct);
    }

    private async Task ChangeAndScheduleAsync(Seeded s, Action<ListingRecord> change)
    {
        await using AsyncServiceScope scope = TestHost.AppScope(Cs, s.Tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        await db.InTransactionAsync(async ct =>
        {
            ListingRecord listing = await db.Listings.SingleAsync(l => l.Id == s.Listing, ct);
            change(listing);
            await db.SaveChangesAsync(ct);
            await db.ScheduleProjectionAsync(new TenantId(s.Tenant), s.Listing, ct);
            return 0;
        }, Ct);
    }

    private async Task<(long Count, long OnDay, long Past)> OccurrenceStatsAsync(Guid listing, int dayOfWeek)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        long count = await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from opportunity.listing_occurrence where listing_id = @l and starts_at >= now()", [("l", listing)]);
        long onDay = await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from opportunity.listing_occurrence where listing_id = @l and starts_at >= now() " +
            "and day_of_week = @d and extract(isodow from starts_at at time zone 'America/New_York')::int % 7 = @d",
            [("l", listing), ("d", (short)dayOfWeek)]);
        long past = await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from opportunity.listing_occurrence where listing_id = @l and starts_at < now() - interval '1 day'",
            [("l", listing)]);
        return (count, onDay, past);
    }

    private async Task<bool> IsPublishedAsync(Guid listing)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        return await Sql.ScalarAsync<bool>(connection, null,
            "select bool_or(is_published) from opportunity.listing_search where listing_id = @l", [("l", listing)]);
    }

    private async Task<long> SiteRowsAsync(Seeded s)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        return await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from opportunity.listing_search where listing_id = @l and site_id = @s", [("l", s.Listing), ("s", s.Site)]);
    }

    private async Task<string[]> FacetsAsync(Guid listing)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await using NpgsqlCommand command = new(
            "select facets from opportunity.listing_search where listing_id = @l", connection);
        command.Parameters.Add(new NpgsqlParameter("l", listing));
        return (string[])(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<FinderPage> SearchAsync(Guid tenant, string text, IReadOnlyList<string>? facets)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(TestHost.As(Cs, TestHost.FinderLogin));
        return await new SqlListingFinder(source).SearchAsync(new SearchQuery
        {
            TenantId = tenant,
            Text = text,
            TextSearchConfig = Locale.TextSearchEnglish,
            AllFacets = facets,
            PageSize = new PageSize(10),
        }, Ct);
    }
}
