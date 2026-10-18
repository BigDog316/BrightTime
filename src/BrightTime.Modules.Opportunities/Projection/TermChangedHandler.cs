using BrightTime.Infrastructure.Jobs;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Projection;

/// <summary>
/// A term was renamed, retired or restored: listings tagged with it are re-projected, so search
/// matches its current name.
/// </summary>
public sealed class TermChangedHandler(OpportunitiesDbContext db) : IJobHandler
{
    public static string JobType => TermChangedJob.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        TenantId tenant = context.TenantId
            ?? throw new InvalidOperationException("A term change must belong to a tenant.");
        TermChangedJob.Payload payload = context.ReadPayload<TermChangedJob.Payload>();

        await db.InTransactionAsync(async ct =>
        {
            List<Guid> listings = await db.ListingTerms.AsNoTracking().Where(x => x.TermId == payload.TermId).Select(x => x.ListingId)
                .Union(db.ListingAccommodations.AsNoTracking().Where(x => x.TermId == payload.TermId).Select(x => x.ListingId))
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (Guid listing in listings)
            {
                await db.ScheduleProjectionAsync(tenant, listing, ct).ConfigureAwait(false);
            }

            return listings.Count;
        }, cancellationToken).ConfigureAwait(false);
    }
}
