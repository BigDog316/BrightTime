using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NpgsqlTypes;

namespace BrightTime.Modules.Opportunities.Persistence;

/// <summary>
/// Writes a listing's search rows: the persistence half of the projection.
/// </summary>
/// <remarks>
/// Replace, not merge. A listing's rows are deleted and rewritten in one transaction, so
/// a site removed from a listing cannot leave a stale row behind for the finder to return.
/// Must run inside the caller's transaction, where the tenant is set; the context refuses
/// otherwise.
/// </remarks>
public sealed class ListingSearchWriter(OpportunitiesDbContext db, IClock clock)
{
    private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

    public async Task ReplaceAsync(
        Guid tenantId,
        Guid listingId,
        IReadOnlyList<ListingSearchRow> rows,
        string textSearchConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(textSearchConfig);

        if (rows.Any(r => r.ListingId != listingId))
        {
            throw new ArgumentException("Every row must belong to the listing being replaced.", nameof(rows));
        }

        await db.ListingSearch
            .Where(r => r.ListingId == listingId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        db.ListingSearch.AddRange(rows.Select(r => ToRecord(tenantId, r, now)));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // to_tsvector needs the configuration by name, which only PostgreSQL can apply.
        // Interpolation here is parameterised by EF, not string-built.
        await db.Database.ExecuteSqlAsync(
            $"""
            update opportunity.listing_search
               set search_vector = to_tsvector({textSearchConfig}::regconfig, title || ' ' || summary || ' ' || fuzzy_text)
             where listing_id = {listingId}
            """,
            cancellationToken).ConfigureAwait(false);
    }

    internal static ListingSearchRecord ToRecord(Guid tenantId, ListingSearchRow row, DateTimeOffset projectedAt) => new()
    {
        Id = SharedKernel.Ids.UuidV7.NewId(),
        TenantId = tenantId,
        ListingId = row.ListingId,
        SiteId = row.SiteId,
        OrgId = row.OrganisationId,
        Slug = row.Slug,
        Kind = row.Kind,
        IsPublished = row.IsPublished,
        IsPrimary = row.IsPrimary,
        Facets = [.. row.Facets],
        AgeRange = row.AgeMin is { } aMin && row.AgeMax is { } aMax
            ? new NpgsqlRange<int>(aMin, true, aMax, true)
            : null,
        GradeRange = row.GradeMin is { } gMin && row.GradeMax is { } gMax
            ? new NpgsqlRange<int>(gMin, true, gMax, true)
            : null,
        CostCents = row.CostCents,
        IsFree = row.IsFree,
        Modality = row.Modality,
        NeighbourhoodId = row.NeighbourhoodId,
        Location = row.Location is { } c ? Wgs84.CreatePoint(new Coordinate(c.Longitude, c.Latitude)) : null,
        SessionWindow = row.SessionWindowStart is { } ws && row.SessionWindowEnd is { } we
            ? new NpgsqlRange<DateTime>(ws.UtcDateTime, true, we.UtcDateTime, false)
            : null,
        NextStartAt = row.NextStartAt,
        NextDeadlineAt = row.NextDeadlineAt,
        IsDropIn = row.IsDropIn,
        FuzzyText = row.FuzzyText,
        Title = row.Title,
        Summary = row.Summary,
        Popularity = (float)row.Popularity,
        PublishedAt = row.PublishedAt,
        ProjectedAt = projectedAt,
    };
}
