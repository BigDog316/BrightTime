using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Modules.Opportunities.Search.Sample;

/// <summary>
/// The listing page over the fictional sample, for development and tests with no database.
/// </summary>
/// <remarks>
/// Every sample listing is published. The rules that matter to a family still hold: another
/// tenant gets nothing, a private address arrives already stripped, and only sessions that
/// have not started are listed. Names are not localised; the sample is English only.
/// </remarks>
public sealed class InMemoryListingDetails(IReadOnlyList<ListingDetail> details, IClock clock) : IListingDetails
{
    private readonly Dictionary<string, ListingDetail> _bySlug =
        details.ToDictionary(d => d.Slug, StringComparer.OrdinalIgnoreCase);

    public Task<ListingDetail?> GetPublishedAsync(
        TenantId tenant,
        string slug,
        string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        cancellationToken.ThrowIfCancellationRequested();

        if (tenant.Value != KettleCatalogue.TenantId || !_bySlug.TryGetValue(slug, out ListingDetail? detail))
        {
            return Task.FromResult<ListingDetail?>(null);
        }

        DateTimeOffset now = clock.UtcNow;
        return Task.FromResult<ListingDetail?>(detail with
        {
            Upcoming = [.. detail.Upcoming.Where(t => t.StartsAt >= now).Take(Details.SqlListingDetails.UpcomingShown)],
            Deadlines = [.. detail.Deadlines.Where(d => d.DueAt >= now)],
        });
    }
}
