using BrightTime.SharedKernel.Primitives;
using Npgsql;

namespace BrightTime.Seed;

/// <summary>How many rows a run added, per table. A second run on the same data adds none.</summary>
internal sealed class SeedCounts
{
    private readonly SortedDictionary<string, (int Added, int Total)> _tables = new(StringComparer.Ordinal);

    public void Add(string table, int added, int total)
    {
        (int a, int t) = _tables.GetValueOrDefault(table);
        _tables[table] = (a + added, t + total);
    }

    public int Added => _tables.Values.Sum(v => v.Added);

    public int AddedTo(string table) => _tables.GetValueOrDefault(table).Added;

    public IEnumerable<string> Lines() =>
        _tables.Select(t => $"  {t.Key,-36} {t.Value.Added,5} added of {t.Value.Total,5}");
}

/// <summary>
/// Writes the seed in one transaction. Every insert is <c>on conflict do nothing</c>, so a
/// re-run leaves existing rows alone: edits made through the application survive it.
/// </summary>
/// <remarks>
/// The seed spans a whole tenant, like a data migration, so it runs as a role that bypasses
/// RLS: a superuser locally, or a login that enters <c>brighttime_migrator</c> (add
/// <c>Options=-c role=brighttime_migrator</c> to its connection string). It refuses to run
/// as anything else, rather than half-seed under a policy that hides its own rows.
/// </remarks>
internal sealed class SeedWriter(NpgsqlDataSource dataSource)
{
    public async Task<SeedCounts> WriteAsync(SeedData data, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureBypassAsync(connection, ct).ConfigureAwait(false);

        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var counts = new SeedCounts();
        Guid t = data.TenantId;

        await RunAsync(connection, tx, counts, "platform.tenant",
            [Row("""
                 insert into platform.tenant (id, slug, display_name, region_name, region_kind, default_locale, timezone, status, student_privacy_pack)
                 values ($1, $2, $3, $4, 'county', 'en', 'America/New_York', 'active', 'ny-ed-law-2d')
                 on conflict do nothing
                 """, t, Kettle.KettleSeed.TenantSlug, Kettle.KettleSeed.DisplayName, Kettle.KettleSeed.RegionName)], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "platform.tenant_domain",
            [.. Kettle.KettleSeed.Hosts.Select((host, i) => Row(
                "insert into platform.tenant_domain (tenant_id, host, is_primary) values ($1, $2, $3) on conflict do nothing",
                t, host, i == 0))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "platform.tenant_locale",
            [.. Locale.Supported.Select((l, i) => Row(
                """
                insert into platform.tenant_locale (tenant_id, locale, is_default, is_rtl, ts_config, sort_order)
                values ($1, $2, $3, $4, $5, $6) on conflict do nothing
                """,
                t, l.Code, l == Locale.Default, l.IsRightToLeft, l.TextSearchConfig, (short)i))], ct).ConfigureAwait(false);

        await WriteGeographyAsync(connection, tx, counts, data, ct).ConfigureAwait(false);
        await WriteTaxonomyAsync(connection, tx, counts, data, ct).ConfigureAwait(false);
        await WriteDirectoryAsync(connection, tx, counts, data, ct).ConfigureAwait(false);
        await WriteAccountsAsync(connection, tx, counts, data, ct).ConfigureAwait(false);
        await WriteListingsAsync(connection, tx, counts, data, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return counts;
    }

    private static async Task EnsureBypassAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand check = new(
            "select rolsuper or rolbypassrls from pg_roles where rolname = current_user", connection);
        if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException(
                "The seed must run as a superuser or as brighttime_migrator, which bypass row-level security. " +
                "Add 'Options=-c role=brighttime_migrator' to the connection string of a login that is a member of it.");
        }
    }

    private static async Task WriteGeographyAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SeedCounts counts, SeedData data, CancellationToken ct)
    {
        Guid t = data.TenantId;

        // Parents first: the county, then each municipality, then its neighbourhoods.
        await RunAsync(connection, tx, counts, "platform.neighbourhood",
            [.. data.Areas.Select(a => a.Box is { } b
                ? Row("""
                      insert into platform.neighbourhood (id, tenant_id, parent_id, code, kind, boundary, centroid, source)
                      values ($1, $2, $3, $4, $5,
                              st_multi(st_makeenvelope($6, $7, $8, $9, 4326))::geography,
                              st_centroid(st_makeenvelope($6, $7, $8, $9, 4326))::geography,
                              'seed:kettle')
                      on conflict do nothing
                      """, a.Id, t, a.ParentId, a.Code, a.Kind, b.West, b.South, b.East, b.North)
                : Row("""
                      insert into platform.neighbourhood (id, tenant_id, parent_id, code, kind, source)
                      values ($1, $2, $3, $4, $5, 'seed:kettle') on conflict do nothing
                      """, a.Id, t, a.ParentId, a.Code, a.Kind))], ct).ConfigureAwait(false);

        // A municipality is the union of its neighbourhoods. Recomputed every run; it is derived.
        await using (NpgsqlCommand union = new(
            """
            update platform.neighbourhood m
               set boundary = u.shape::geography,
                   centroid = st_centroid(u.shape)::geography,
                   land_area_sqkm = round((st_area(u.shape::geography) / 1e6)::numeric, 3)
              from (select parent_id, st_multi(st_union(boundary::geometry)) as shape
                      from platform.neighbourhood
                     where tenant_id = $1 and kind = 'neighbourhood'
                     group by parent_id) u
             where m.id = u.parent_id
            """, connection, tx))
        {
            union.Parameters.Add(new NpgsqlParameter { Value = t });
            await union.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await RunAsync(connection, tx, counts, "platform.neighbourhood_name",
            [.. data.Areas.Select(a => Row(
                "insert into platform.neighbourhood_name (neighbourhood_id, locale, name) values ($1, 'en', $2) on conflict do nothing",
                a.Id, a.Name))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "platform.school_district",
            [.. data.Districts.Select(d => Row(
                """
                insert into platform.school_district (id, tenant_id, name, boundary)
                select $1, $2, $3, st_multi(st_union(boundary::geometry))::geography
                  from platform.neighbourhood where id = any($4)
                on conflict do nothing
                """, d.Id, t, d.Name, d.MunicipalityIds.ToArray()))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "platform.school",
            [.. data.Schools.Select(s => Row(
                """
                insert into platform.school (id, tenant_id, district_id, name, grade_min, grade_max, location, neighbourhood_id)
                values ($1, $2, $3, $4, $5, $6, st_setsrid(st_makepoint($7, $8), 4326)::geography, $9)
                on conflict do nothing
                """, s.Id, t, s.DistrictId, s.Name, s.GradeMin, s.GradeMax, s.Lon, s.Lat, s.NeighbourhoodId))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "landscape.transit_feed",
            [Row("""
                 insert into landscape.transit_feed (id, tenant_id, agency_name, feed_url)
                 values ($1, $2, $3, $4) on conflict do nothing
                 """, data.Transit.FeedId, t, data.Transit.AgencyName, data.Transit.FeedUrl)], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "landscape.transit_stop",
            [.. data.Transit.Stops.Select(s => Row(
                """
                insert into landscape.transit_stop (id, tenant_id, feed_id, gtfs_stop_id, name, location, is_wheelchair_boarding)
                values ($1, $2, $3, $4, $5, st_setsrid(st_makepoint($6, $7), 4326)::geography, $8)
                on conflict do nothing
                """, s.Id, t, data.Transit.FeedId, s.GtfsId, s.Name, s.Lon, s.Lat, s.WheelchairBoarding))], ct).ConfigureAwait(false);
    }

    private static async Task WriteTaxonomyAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SeedCounts counts, SeedData data, CancellationToken ct)
    {
        // Categories before interests: an interest's parent is a category.
        await RunAsync(connection, tx, counts, "platform.taxonomy_term",
            [.. data.Terms.OrderBy(x => x.ParentId is not null).Select(x => Row(
                """
                insert into platform.taxonomy_term (id, tenant_id, vocabulary, parent_id, code, sort_order, is_interest)
                values ($1, $2, $3, $4, $5, $6, $7) on conflict do nothing
                """, x.Id, data.TenantId, x.Vocabulary, x.ParentId, x.Code, x.SortOrder, x.IsInterest))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "platform.taxonomy_term_name",
            [.. data.Terms.SelectMany(x => x.Names.Select(n => Row(
                "insert into platform.taxonomy_term_name (term_id, locale, name) values ($1, $2, $3) on conflict do nothing",
                x.Id, n.Key, n.Value)))], ct).ConfigureAwait(false);
    }

    private static async Task WriteDirectoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SeedCounts counts, SeedData data, CancellationToken ct)
    {
        await RunAsync(connection, tx, counts, "directory.organisation",
            [.. data.Organisations.Select(o => Row(
                """
                insert into directory.organisation
                  (id, tenant_id, legal_name, display_name, slug, status, org_kind, website_url, mission,
                   public_email, public_phone, languages_served, submitted_at, approved_at)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                on conflict do nothing
                """, o.Id, data.TenantId, o.LegalName, o.DisplayName, o.Slug, o.Status, o.Kind, o.WebsiteUrl, o.Mission,
                o.PublicEmail, o.PublicPhone, o.LanguagesServed.ToArray(), o.SubmittedAt, o.ApprovedAt))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "directory.site",
            [.. data.Sites.Select(s => Row(
                """
                insert into directory.site
                  (id, tenant_id, org_id, name, address_line1, locality, region_code, postal_code, location,
                   geocode_accuracy, neighbourhood_id, is_address_public, transit_note, is_wheelchair_accessible)
                values ($1, $2, $3, $4, $5, $6, 'NY', $7, st_setsrid(st_makepoint($8, $9), 4326)::geography,
                        'manual', $10, $11, $12, $13)
                on conflict do nothing
                """, s.Id, data.TenantId, s.OrgId, s.Name, s.AddressLine1, s.Locality, s.PostalCode, s.Lon, s.Lat,
                s.NeighbourhoodId, s.IsAddressPublic, s.TransitNote, s.IsWheelchairAccessible))], ct).ConfigureAwait(false);
    }

    private static async Task WriteAccountsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SeedCounts counts, SeedData data, CancellationToken ct)
    {
        Guid t = data.TenantId;

        await RunAsync(connection, tx, counts, "identity.app_role",
            [.. data.Roles.Select(r => Row(
                """
                insert into identity.app_role (id, tenant_id, name, normalized_name, concurrency_stamp, is_system)
                values ($1, $2, $3, upper($3), $4, true) on conflict do nothing
                """, r.Id, t, r.Name, r.Id.ToString("N")))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "identity.app_role_claim",
            [.. data.Roles.SelectMany(r => r.Permissions.Select(p => Row(
                """
                insert into identity.app_role_claim (role_id, claim_type, claim_value)
                values ($1, $2, $3) on conflict do nothing
                """, r.Id, Modules.Identity.Authorization.BrightTimeClaims.Permission, p)))], ct).ConfigureAwait(false);

        // No password: these accounts are reached through development sign-in (D14) until
        // Identity sign-in exists, and through a password reset after that.
        await RunAsync(connection, tx, counts, "identity.app_user",
            [.. data.Users.Select(u => Row(
                """
                insert into identity.app_user
                  (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                   security_stamp, concurrency_stamp, account_kind, status, locale)
                values ($1, $2, $3, upper($3), $3, upper($3), true, $4, $5, 'adult', 'active', 'en')
                on conflict do nothing
                """, u.Id, t, u.Email, SeedIds.For("stamp/" + u.Email).ToString("N"), u.Id.ToString("N")))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "identity.app_user_role",
            [.. data.Users.SelectMany(u => u.Roles.Select(r => Row(
                "insert into identity.app_user_role (user_id, role_id) values ($1, $2) on conflict do nothing",
                u.Id, Kettle.KettleAccounts.RoleId(r))))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "directory.org_membership",
            [.. data.Users.SelectMany(u => u.Memberships.Select(m => Row(
                """
                insert into directory.org_membership (id, tenant_id, org_id, user_id, org_role, status, invited_at, joined_at)
                values ($1, $2, $3, $4, $5, 'active', now() - interval '30 days', now() - interval '29 days')
                on conflict do nothing
                """, m.Id, t, m.OrgId, u.Id, m.OrgRole)))], ct).ConfigureAwait(false);
    }

    private static async Task WriteListingsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SeedCounts counts, SeedData data, CancellationToken ct)
    {
        Guid t = data.TenantId;

        await RunAsync(connection, tx, counts, "opportunity.listing",
            [.. data.Listings.Select(l => Row(
                """
                insert into opportunity.listing
                  (id, tenant_id, org_id, kind, slug, title, summary, description, primary_language, status,
                   age_min, age_max, grade_min, grade_max, modality, virtual_url, is_drop_in, meals_provided,
                   transport_provided, transport_note, cost_model, cost_cents_min, cost_cents_max, sliding_scale_note,
                   registration_mode, external_url, capacity_total, waitlist_enabled, submitted_at, published_at,
                   unpublished_at, expires_at, source, season_label, contact_email, contact_phone, accessibility_note,
                   reviewed_at, reviewed_by, review_note, created_at, updated_at)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20,
                        $21, $22, $23, $24, $25, $26, $27, $28, $29, $30, $31, $32, 'manual', $33, $34, $35, $36,
                        $37, case when $37::timestamptz is null and $39::text is null then null else $38::uuid end, $39,
                        coalesce($30::timestamptz, $29::timestamptz, now()), coalesce($30::timestamptz, $29::timestamptz, now()))
                on conflict do nothing
                """,
                l.Id, t, l.OrgId, l.Kind, l.Slug, l.Title, l.Summary, l.Description, l.PrimaryLanguage, l.Status,
                l.AgeMin, l.AgeMax, l.GradeMin, l.GradeMax, l.Modality, l.VirtualUrl, l.IsDropIn, l.MealsProvided,
                l.TransportProvided, l.TransportNote, l.CostModel, l.CostCentsMin, l.CostCentsMax, l.SlidingScaleNote,
                l.RegistrationMode, l.ExternalUrl, l.CapacityTotal, l.WaitlistEnabled, l.SubmittedAt, l.PublishedAt,
                l.UnpublishedAt, l.ExpiresAt, l.SeasonLabel, l.ContactEmail, l.ContactPhone, l.AccessibilityNote,
                l.ReviewedAt, Kettle.KettleAccounts.OrganiserId, l.ReviewNote))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "opportunity.listing_site",
            [.. data.Listings.SelectMany(l => l.SiteIds.Select(s => Row(
                "insert into opportunity.listing_site (listing_id, site_id) values ($1, $2) on conflict do nothing", l.Id, s)))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "opportunity.listing_term",
            [.. data.Listings.SelectMany(l => l.TermIds.Select((term, i) => Row(
                "insert into opportunity.listing_term (listing_id, term_id, is_primary) values ($1, $2, $3) on conflict do nothing",
                l.Id, term, i == 0)))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "opportunity.listing_accommodation",
            [.. data.Listings.SelectMany(l => l.AccommodationTermIds.Select(a => Row(
                "insert into opportunity.listing_accommodation (listing_id, term_id) values ($1, $2) on conflict do nothing",
                l.Id, a)))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "opportunity.listing_language",
            [.. data.Listings.SelectMany(l => l.Languages.Select(code => Row(
                "insert into opportunity.listing_language (listing_id, language_code) values ($1, $2) on conflict do nothing",
                l.Id, code)))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "opportunity.listing_session",
            [.. data.Listings.SelectMany(l => l.Sessions.Select(s => Row(
                """
                insert into opportunity.listing_session
                  (id, tenant_id, listing_id, site_id, timezone, starts_on, ends_on, start_time, end_time, rrule, exdates)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) on conflict do nothing
                """, s.Id, t, l.Id, s.SiteId, Kettle.KettleListings.Zone, s.StartsOn, s.EndsOn, s.StartTime, s.EndTime,
                s.Rrule, s.Exdates.ToArray())))], ct).ConfigureAwait(false);

        await RunAsync(connection, tx, counts, "opportunity.listing_deadline",
            [.. data.Listings.SelectMany(l => l.Deadlines.Select(d => Row(
                """
                insert into opportunity.listing_deadline (id, listing_id, deadline_kind, due_at, label)
                values ($1, $2, $3, $4, $5) on conflict do nothing
                """, d.Id, l.Id, d.Kind, d.DueAt, d.Label)))], ct).ConfigureAwait(false);

        // Written for the seed, not by a translator: stored as machine translation so the
        // review workflow treats it as unreviewed.
        await RunAsync(connection, tx, counts, "opportunity.listing_translation",
            [.. data.Listings.SelectMany(l => l.Translations.Select(x => Row(
                """
                insert into opportunity.listing_translation (listing_id, locale, title, summary, description, translation_source)
                values ($1, $2, $3, $4, $5, 'machine') on conflict do nothing
                """, l.Id, x.Locale, x.Title, x.Summary, x.Description)))], ct).ConfigureAwait(false);
    }

    private static NpgsqlBatchCommand Row(string sql, params object?[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (object? value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return command;
    }

    private static async Task RunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        SeedCounts counts,
        string table,
        IReadOnlyList<NpgsqlBatchCommand> rows,
        CancellationToken ct)
    {
        const int chunk = 500;
        for (int offset = 0; offset < rows.Count; offset += chunk)
        {
            await using NpgsqlBatch batch = new(connection, tx);
            foreach (NpgsqlBatchCommand row in rows.Skip(offset).Take(chunk))
            {
                batch.BatchCommands.Add(row);
            }

            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            int added = 0;
            foreach (NpgsqlBatchCommand row in batch.BatchCommands)
            {
                added += row.RecordsAffected;
            }

            counts.Add(table, added, batch.BatchCommands.Count);
        }
    }
}
