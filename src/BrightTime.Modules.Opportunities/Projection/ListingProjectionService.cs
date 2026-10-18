using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Projection;

/// <summary>
/// Rebuilds one listing's occurrences and search rows from its current state.
/// </summary>
/// <remarks>
/// <para>
/// Reads, then writes. The listing and its children are read in one transaction; sites,
/// organisation name and taxonomy come from Directory and Platform through their contracts,
/// each in its own; then occurrences and search rows are replaced together in one write
/// transaction. If the listing changes between the read and the write, its next projection
/// job (which the debounce guarantees is queued) corrects it.
/// </para>
/// <para>
/// Occurrences from now on are replaced; earlier ones are kept, because attendance history
/// hangs off them.
/// </para>
/// </remarks>
public sealed class ListingProjectionService(
    OpportunitiesDbContext db,
    ListingSearchWriter writer,
    IDirectoryQueries directory,
    ITaxonomyQueries taxonomy,
    IClock clock)
{
    /// <summary>How far ahead occurrences are materialised.</summary>
    public const int HorizonDays = 180;

    private const string TaxonomyLocale = "en";

    public async Task ProjectAsync(TenantId tenant, Guid listingId, CancellationToken cancellationToken = default)
    {
        Snapshot? snapshot = await db.InTransactionAsync(ct => LoadAsync(listingId, ct), cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            // Deleted: its search rows and occurrences went with it (ON DELETE CASCADE).
            return;
        }

        IReadOnlyList<SiteSummary> sites = await directory
            .GetSitesAsync(snapshot.SiteIds, cancellationToken).ConfigureAwait(false);
        string organisationName = await directory
            .GetOrganisationNameAsync(new OrganisationId(snapshot.Listing.OrgId), cancellationToken).ConfigureAwait(false)
            ?? string.Empty;

        // A suspended or withdrawn organisation's listings leave search with it.
        bool organisationApproved = await directory
            .GetStatusAsync(new OrganisationId(snapshot.Listing.OrgId), cancellationToken).ConfigureAwait(false) == OrganisationStatus.Approved;
        IReadOnlyList<TermSummary> terms = await taxonomy
            .GetTermsAsync([.. snapshot.TermIds, .. snapshot.AccommodationTermIds], TaxonomyLocale, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        List<(ListingSessionRecord Session, Occurrence Occurrence)> future = Expand(snapshot.Sessions, now);

        await db.InTransactionAsync(async ct =>
        {
            await ReplaceFutureOccurrencesAsync(snapshot, future, now, ct).ConfigureAwait(false);

            ProjectionSource source = BuildSource(snapshot, sites, organisationName, organisationApproved, terms, future, now);
            IReadOnlyList<ListingSearchRow> rows = ListingSearchProjector.Project(source);

            string config = Locale.Resolve(snapshot.Listing.PrimaryLanguage).TextSearchConfig;
            await writer.ReplaceAsync(tenant.Value, listingId, rows, config, ct).ConfigureAwait(false);

            // Queued from here rather than by the editor, so every route that changes the
            // listing (editor, review, import, clone) gets the boundary job, and a job that runs
            // a moment early queues itself again rather than leaving the listing stale.
            if (NextBoundary(snapshot.Listing, now) is { } boundary)
            {
                await db.ScheduleProjectionAtAsync(tenant, listingId, boundary, ct).ConfigureAwait(false);
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Snapshot(
        ListingRecord Listing,
        IReadOnlyList<Guid> SiteIds,
        IReadOnlyList<Guid> TermIds,
        IReadOnlyList<Guid> AccommodationTermIds,
        IReadOnlyList<string> Languages,
        IReadOnlyList<DateTimeOffset> Deadlines,
        IReadOnlyList<ListingSessionRecord> Sessions);

    private async Task<Snapshot?> LoadAsync(Guid listingId, CancellationToken ct)
    {
        ListingRecord? listing = await db.Listings.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return null;
        }

        return new Snapshot(
            listing,
            await db.ListingSites.AsNoTracking().Where(x => x.ListingId == listingId)
                .Select(x => x.SiteId).ToListAsync(ct).ConfigureAwait(false),
            await db.ListingTerms.AsNoTracking().Where(x => x.ListingId == listingId)
                .Select(x => x.TermId).ToListAsync(ct).ConfigureAwait(false),
            await db.ListingAccommodations.AsNoTracking().Where(x => x.ListingId == listingId)
                .Select(x => x.TermId).ToListAsync(ct).ConfigureAwait(false),
            await db.ListingLanguages.AsNoTracking().Where(x => x.ListingId == listingId)
                .Select(x => x.LanguageCode).ToListAsync(ct).ConfigureAwait(false),
            await db.ListingDeadlines.AsNoTracking().Where(x => x.ListingId == listingId)
                .Select(x => x.DueAt).ToListAsync(ct).ConfigureAwait(false),
            await db.Sessions.Where(s => s.ListingId == listingId && !s.IsCancelled)
                .ToListAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Occurrences from <paramref name="now"/> to the horizon, for every live session.</summary>
    private static List<(ListingSessionRecord, Occurrence)> Expand(IReadOnlyList<ListingSessionRecord> sessions, DateTimeOffset now)
    {
        List<(ListingSessionRecord, Occurrence)> result = [];

        foreach (ListingSessionRecord session in sessions)
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(session.Timezone);
            DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

            var rule = new SessionRule(
                session.Timezone,
                session.StartsOn,
                session.EndsOn,
                session.StartTime ?? TimeOnly.MinValue,
                session.EndTime ?? (session.IsAllDay ? TimeOnly.MaxValue : session.StartTime ?? TimeOnly.MinValue),
                session.Rrule,
                session.Exdates,
                session.IsAllDay);

            DateOnly from = today > session.StartsOn ? today : session.StartsOn;
            foreach (Occurrence occurrence in OccurrenceExpander.Expand(rule, from, today.AddDays(HorizonDays)))
            {
                // Today's earlier sessions are history, not future: they are kept, not regenerated.
                if (occurrence.StartsAt >= now)
                {
                    result.Add((session, occurrence));
                }
            }
        }

        return result;
    }

    private async Task ReplaceFutureOccurrencesAsync(
        Snapshot snapshot,
        List<(ListingSessionRecord Session, Occurrence Occurrence)> future,
        DateTimeOffset now,
        CancellationToken ct)
    {
        Guid listingId = snapshot.Listing.Id;

        await db.Occurrences
            .Where(o => o.ListingId == listingId && o.StartsAt >= now)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        db.Occurrences.AddRange(future.Select(f => new ListingOccurrenceRecord
        {
            TenantId = snapshot.Listing.TenantId,
            ListingId = listingId,
            SessionId = f.Session.Id,
            SiteId = f.Session.SiteId,
            StartsAt = f.Occurrence.StartsAt,
            EndsAt = f.Occurrence.EndsAt,
            DayOfWeek = (short)f.Occurrence.DayOfWeek,
        }));

        DateOnly through = DateOnly.FromDateTime(now.UtcDateTime).AddDays(HorizonDays);
        // The sessions were loaded with tracking, so setting the field is enough.
        foreach (ListingSessionRecord session in snapshot.Sessions)
        {
            session.ExpandedThrough = through;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ProjectionSource BuildSource(
        Snapshot snapshot,
        IReadOnlyList<SiteSummary> sites,
        string organisationName,
        bool organisationApproved,
        IReadOnlyList<TermSummary> terms,
        List<(ListingSessionRecord Session, Occurrence Occurrence)> future,
        DateTimeOffset now)
    {
        ListingRecord listing = snapshot.Listing;
        HashSet<Guid> accommodationIds = [.. snapshot.AccommodationTermIds];

        string[] Codes(string vocabulary, bool accommodation) =>
        [
            .. terms.Where(t => t.Vocabulary == vocabulary && accommodationIds.Contains(t.Id) == accommodation)
                .Select(t => t.Code),
        ];

        IEnumerable<SiteSummary> activeSites = sites.Where(s => s.IsActive);

        return new ProjectionSource
        {
            ListingId = listing.Id,
            OrganisationId = listing.OrgId,
            OrganisationName = organisationName,
            IsPublished = organisationApproved && IsLive(listing, now),
            PublishedAt = listing.PublishedAt,
            Title = listing.Title,
            Slug = listing.Slug,
            Summary = listing.Summary,
            CategoryNames = [.. terms.Where(t => t.Vocabulary is "category" or "interest").Select(t => t.Name ?? t.Code)],
            Sites = [.. activeSites.Select(s => new SiteSource(s.SiteId, s.NeighbourhoodId, s.Location))],
            OccurrenceStarts = [.. future.Select(f => f.Occurrence.StartsAt)],
            OccurrenceEnds = [.. future.Select(f => f.Occurrence.EndsAt)],
            Deadlines = [.. snapshot.Deadlines.Where(d => d >= now)],
            Popularity = 0,
            CostCents = listing.CostCentsMin ?? 0,
            IsFree = listing.CostModel == CostModel.Free.ToDbValue(),
            Facets = new FacetInput
            {
                Kind = Parse<ListingKind>(listing.Kind, k => k.ToDbValue()),
                Modality = Parse<Modality>(listing.Modality, m => m.ToDbValue()),
                CostModel = Parse<CostModel>(listing.CostModel, c => c.ToDbValue()),
                AgeMin = listing.AgeMin,
                AgeMax = listing.AgeMax,
                GradeMin = listing.GradeMin,
                GradeMax = listing.GradeMax,
                Categories = Codes("category", accommodation: false),
                Interests = Codes("interest", accommodation: false),
                Accommodations = Codes("accommodation", accommodation: true),
                Languages = snapshot.Languages.ToArray(),
                DaysOfWeek = [.. future.Select(f => f.Occurrence.DayOfWeek).Distinct()],
                OrganisationId = listing.OrgId,
                MealsProvided = listing.MealsProvided,
                TransportProvided = listing.TransportProvided,
                IsDropIn = listing.IsDropIn,
            },
        };
    }

    /// <summary>Published, past its show-from moment, and not yet taken down.</summary>
    internal static bool IsLive(ListingRecord listing, DateTimeOffset now) =>
        listing.Status == ListingStatus.Published.ToDbValue()
        && (listing.PublishAt is null || listing.PublishAt <= now)
        && (listing.ExpiresAt is null || listing.ExpiresAt > now);

    /// <summary>
    /// The next moment this listing's visibility changes by itself, if any. Nothing else
    /// re-projects a listing when it reaches its show-from or take-down time.
    /// </summary>
    internal static DateTimeOffset? NextBoundary(ListingRecord listing, DateTimeOffset now)
    {
        if (listing.Status != ListingStatus.Published.ToDbValue())
        {
            return null;
        }

        if (listing.PublishAt is { } show && show > now)
        {
            return show;
        }

        return listing.ExpiresAt is { } end && end > now ? end : null;
    }

    /// <summary>Reads a CHECK-constrained text column back into its enum. An unknown value is a schema/code mismatch.</summary>
    private static T Parse<T>(string value, Func<T, string> toDb) where T : struct, Enum
    {
        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(toDb(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"'{value}' is not a valid {typeof(T).Name} in the listing table.");
    }
}
