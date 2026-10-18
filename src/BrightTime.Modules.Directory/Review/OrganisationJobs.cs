using BrightTime.Infrastructure.Jobs;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Directory.Review;

internal static class OrganisationJobs
{
    /// <summary>Queues <see cref="OrganisationChangedJob"/> in the caller's transaction (D24).</summary>
    public static Task<bool> QueueOrganisationChangedAsync(this DirectoryDbContext db, TenantId tenant, Guid organisation, CancellationToken ct) =>
        db.EnqueueJobAsync(new JobRequest
        {
            JobType = OrganisationChangedJob.JobType,
            TenantId = tenant,
            Payload = new OrganisationChangedJob.Payload(organisation),
            UniqueKey = OrganisationChangedJob.UniqueKey(organisation),
        }, ct);
}
