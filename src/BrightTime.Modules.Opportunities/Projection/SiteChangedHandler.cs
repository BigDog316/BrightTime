using BrightTime.Infrastructure.Jobs;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Projection;

/// <summary>
/// A site moved, was renamed into another neighbourhood, or closed: every listing held there
/// is re-projected, so search shows it in the right place, or no longer shows it there.
/// </summary>
public sealed class SiteChangedHandler(OpportunitiesDbContext db) : IJobHandler
{
    public static string JobType => SiteChangedJob.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        TenantId tenant = context.TenantId
            ?? throw new InvalidOperationException("A site change must belong to a tenant.");
        SiteChangedJob.Payload payload = context.ReadPayload<SiteChangedJob.Payload>();

        await db.InTransactionAsync(async ct =>
        {
            List<Guid> listings = await db.ListingSites.AsNoTracking()
                .Where(x => x.SiteId == payload.SiteId)
                .Select(x => x.ListingId)
                .ToListAsync(ct).ConfigureAwait(false);

            // Each projection is its own debounced job, so one slow listing does not hold up the rest.
            foreach (Guid listing in listings)
            {
                await db.ScheduleProjectionAsync(tenant, listing, ct).ConfigureAwait(false);
            }

            return listings.Count;
        }, cancellationToken).ConfigureAwait(false);
    }
}
