using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Details;

/// <summary>A listing's materialised occurrences, under the tenant's row-level security.</summary>
public sealed class ListingOccurrences(OpportunitiesDbContext db) : IListingOccurrences
{
    public Task<IReadOnlyList<ListingTime>> GetAsync(Guid listingId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ListingTime>>(async ct =>
            [.. await db.Occurrences.AsNoTracking()
                .Where(o => o.ListingId == listingId && !o.IsCancelled && o.StartsAt >= from && o.StartsAt < to)
                .OrderBy(o => o.StartsAt)
                .Select(o => new ListingTime(o.StartsAt, o.EndsAt))
                .ToListAsync(ct).ConfigureAwait(false)], cancellationToken);

    public Task<IReadOnlyList<OrganisationMeeting>> ForOrganisationAsync(Guid orgId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<OrganisationMeeting>>(async ct =>
            [.. await (from o in db.Occurrences.AsNoTracking()
                       join l in db.Listings.AsNoTracking() on o.ListingId equals l.Id
                       where l.OrgId == orgId && l.Status == "published" && !o.IsCancelled && o.StartsAt >= @from && o.StartsAt < to
                       orderby o.StartsAt, l.Title
                       select new OrganisationMeeting(l.Id, l.Title, o.StartsAt, o.EndsAt))
                .Take(200).ToListAsync(ct).ConfigureAwait(false)], cancellationToken);

    public Task<IReadOnlyList<ListingDeadlineDue>> DeadlinesAsync(IReadOnlyCollection<Guid> listingIds, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listingIds);
        if (listingIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ListingDeadlineDue>>([]);
        }

        Guid[] ids = [.. listingIds];
        return db.InTransactionAsync<IReadOnlyList<ListingDeadlineDue>>(async ct =>
            [.. await (from d in db.ListingDeadlines.AsNoTracking()
                       join l in db.Listings.AsNoTracking() on d.ListingId equals l.Id
                       where ids.Contains(l.Id) && l.Status == "published"
                             && (l.PublishAt == null || l.PublishAt <= @from) && (l.ExpiresAt == null || l.ExpiresAt > @from)
                             && d.DueAt >= @from && d.DueAt < to
                       orderby d.DueAt
                       select new ListingDeadlineDue(d.ListingId, d.DeadlineKind, d.DueAt, d.Label))
                .Take(200).ToListAsync(ct).ConfigureAwait(false)], cancellationToken);
    }
}
