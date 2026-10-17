using BrightTime.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Infrastructure.Jobs;

/// <summary>Enqueues work in the caller's own transaction.</summary>
public static class JobEnqueue
{
    /// <summary>
    /// Writes the job through <paramref name="db"/>'s connection and current transaction, so the
    /// job exists only if the change that caused it commits. Returns false when the request
    /// collapsed into a job with the same <see cref="JobRequest.UniqueKey"/> that has not started.
    /// </summary>
    /// <remarks>
    /// The application role may enqueue only for its own tenant: the RLS WITH CHECK on
    /// platform.job refuses any other. Must run inside a transaction; the context refuses otherwise.
    /// </remarks>
    public static async Task<bool> EnqueueJobAsync(
        this BrightTimeDbContext db,
        JobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);

        Guid id = SharedKernel.Ids.UuidV7.NewId();
        Guid? tenant = request.TenantId?.Value;
        DateTimeOffset? runAfter = request.RunAfter;
        string payload = JobExecutionContext.WritePayload(request.Payload);

        int inserted = await db.Database.ExecuteSqlAsync(
            $"""
            insert into platform.job (id, tenant_id, queue, job_type, payload, priority, max_attempts, unique_key, run_after)
            values ({id}, {tenant}, {request.Queue}, {request.JobType}, {payload}::jsonb,
                    {request.Priority}, {request.MaxAttempts}, {request.UniqueKey}, coalesce({runAfter}, now()))
            on conflict (unique_key) where unique_key is not null and state = 'ready' do nothing
            """,
            cancellationToken).ConfigureAwait(false);

        return inserted == 1;
    }
}
