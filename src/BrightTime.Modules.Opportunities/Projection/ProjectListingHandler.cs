using BrightTime.Infrastructure.Jobs;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Opportunities.Projection;

/// <summary>The job that keeps a listing's search rows and occurrences current.</summary>
public sealed class ProjectListingHandler(ListingProjectionService projection) : IJobHandler
{
    public static string JobType => "opportunities.project_listing";

    public sealed record Payload(Guid ListingId);

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        TenantId tenant = context.TenantId
            ?? throw new InvalidOperationException("A listing projection must belong to a tenant.");

        Payload payload = context.ReadPayload<Payload>();
        await projection.ProjectAsync(tenant, payload.ListingId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Scheduling and registration for listing projection.</summary>
public static class ListingProjection
{
    /// <summary>
    /// Queues a projection of <paramref name="listingId"/> in the caller's transaction, so it exists
    /// only if the change commits. Repeated edits collapse into one waiting job.
    /// </summary>
    public static Task<bool> ScheduleProjectionAsync(
        this OpportunitiesDbContext db,
        TenantId tenant,
        Guid listingId,
        CancellationToken cancellationToken = default) =>
        db.EnqueueJobAsync(new JobRequest
        {
            JobType = ProjectListingHandler.JobType,
            TenantId = tenant,
            Payload = new ProjectListingHandler.Payload(listingId),
            UniqueKey = $"{ProjectListingHandler.JobType}:{listingId:N}",
        }, cancellationToken);

    /// <summary>Registers the projection service and its job handler. Module contexts and queries are registered by the host.</summary>
    public static IServiceCollection AddListingProjection(this IServiceCollection services)
    {
        services.AddScoped<ListingSearchWriter>();
        services.AddScoped<ListingProjectionService>();
        services.AddJobHandler<ProjectListingHandler>();
        return services;
    }
}
