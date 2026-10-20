using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Identity.Consent;

/// <summary>
/// Recomputes a young person's capabilities on the day their age band changes, booked by the
/// previous recompute (<see cref="Guardianship"/>). The day a child turns 13 is the day the
/// under-13 floor lifts; nothing else would notice.
/// </summary>
public sealed class RecomputeCapabilitiesHandler(Guardianship guardianship) : IJobHandler
{
    public static string JobType => "identity.recompute_capabilities";

    public sealed record Payload(Guid UserId);

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = context.TenantId ?? throw new InvalidOperationException("A capability recompute must belong to a tenant.");

        Payload payload = context.ReadPayload<Payload>();
        await guardianship.RecomputeAsync(new UserId(payload.UserId), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Registration for the worker host.</summary>
public static class GuardianshipJobs
{
    /// <summary>
    /// The guardianship service and its jobs. The host registers <see cref="Persistence.IdentityDbContext"/>
    /// on its own login. A job has no request, so audit rows carry no client hashes.
    /// </summary>
    public static IServiceCollection AddGuardianshipJobs(this IServiceCollection services)
    {
        services.AddScoped<Guardianship>();
        services.AddJobHandler<RecomputeCapabilitiesHandler>();
        return services;
    }
}
