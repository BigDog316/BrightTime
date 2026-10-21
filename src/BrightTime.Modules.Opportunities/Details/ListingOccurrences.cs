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
}
