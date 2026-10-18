using BrightTime.Infrastructure.Jobs;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Projection;

/// <summary>
/// An organisation was approved, suspended, reinstated or withdrawn: each of its listings is
/// re-projected, so search shows it only while the organisation is approved.
/// </summary>
public sealed class OrganisationChangedHandler(OpportunitiesDbContext db) : IJobHandler
{
    public static string JobType => OrganisationChangedJob.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        TenantId tenant = context.TenantId
            ?? throw new InvalidOperationException("An organisation change must belong to a tenant.");
        OrganisationChangedJob.Payload payload = context.ReadPayload<OrganisationChangedJob.Payload>();

        await db.InTransactionAsync(async ct =>
        {
            List<Guid> listings = await db.Listings.AsNoTracking()
                .Where(l => l.OrgId == payload.OrganisationId)
                .Select(l => l.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (Guid listing in listings)
            {
                await db.ScheduleProjectionAsync(tenant, listing, ct).ConfigureAwait(false);
            }

            return listings.Count;
        }, cancellationToken).ConfigureAwait(false);
    }
}
