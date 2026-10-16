using Npgsql;

namespace BrightTime.Infrastructure.Jobs;

/// <summary>
/// The worker's side of <c>platform.job</c>: claim, complete, fail, reap.
/// </summary>
/// <remarks>
/// <para>
/// Connects as the jobs login, which the <c>job_worker_claims</c> policy lets see every
/// tenant's jobs. All times come from the database clock (<c>now()</c>), so workers on
/// different hosts agree on what is due and what has expired.
/// </para>
/// <para>
/// Complete and fail only touch a job still locked by the same worker. A worker whose lock
/// expired, and whose job the reaper has handed to someone else, cannot overwrite it.
/// </para>
/// </remarks>
public sealed class JobStore(NpgsqlDataSource dataSource)
{
    /// <summary>Claims up to <paramref name="batchSize"/> due jobs. Concurrent workers never claim the same job.</summary>
    public async Task<IReadOnlyList<JobRecord>> ClaimAsync(
        string queue,
        string workerId,
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            """
            with next as (
              select id from platform.job
              where state = 'ready' and queue = @queue and run_after <= now()
              order by priority, run_after
              for update skip locked
              limit @batch
            )
            update platform.job j
               set state = 'running', locked_by = @worker, locked_at = now(),
                   lock_expires_at = now() + @lock, attempts = j.attempts + 1
              from next
             where j.id = next.id
            returning j.id, j.tenant_id, j.queue, j.job_type, j.payload::text, j.state, j.priority,
                      j.run_after, j.attempts, j.max_attempts, j.locked_by, j.locked_at,
                      j.lock_expires_at, j.unique_key, j.created_at
            """);
        command.Parameters.Add(new NpgsqlParameter("queue", queue));
        command.Parameters.Add(new NpgsqlParameter("worker", workerId));
        command.Parameters.Add(new NpgsqlParameter("batch", batchSize));
        command.Parameters.Add(new NpgsqlParameter("lock", lockDuration));

        List<JobRecord> jobs = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    /// <summary>
    /// Maps the current row. Synchronous accessors are safe here: after ReadAsync, Npgsql has the
    /// whole row buffered in its default (non-sequential) mode, as in SqlListingFinder.
    /// </summary>
    private static JobRecord ReadJob(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        TenantId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
        Queue = reader.GetString(2),
        JobType = reader.GetString(3),
        Payload = reader.GetString(4),
        State = reader.GetString(5),
        Priority = reader.GetInt16(6),
        RunAfter = reader.GetFieldValue<DateTimeOffset>(7),
        Attempts = reader.GetInt16(8),
        MaxAttempts = reader.GetInt16(9),
        LockedBy = reader.GetString(10),
        LockedAt = reader.GetFieldValue<DateTimeOffset>(11),
        LockExpiresAt = reader.GetFieldValue<DateTimeOffset>(12),
        UniqueKey = reader.IsDBNull(13) ? null : reader.GetString(13),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
    };

    /// <summary>Marks a job done. False if this worker no longer holds it.</summary>
    public async Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            """
            update platform.job
               set state = 'succeeded', completed_at = now(),
                   locked_by = null, locked_at = null, lock_expires_at = null
             where id = @id and locked_by = @worker and state = 'running'
            """);
        command.Parameters.Add(new NpgsqlParameter("id", jobId));
        command.Parameters.Add(new NpgsqlParameter("worker", workerId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Records a failure. The job retries after <paramref name="backoff"/>, goes dead once its
    /// attempts are spent, or is cancelled when a newer request for the same key is already
    /// waiting. Returns the state it ended in, or null if this worker no longer held it.
    /// </summary>
    public async Task<string?> FailAsync(
        Guid jobId,
        string workerId,
        string error,
        TimeSpan backoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        await using NpgsqlCommand command = dataSource.CreateCommand(
            """
            with decided as (
              select j.id,
                     case when j.attempts >= j.max_attempts then 'dead'
                          when j.unique_key is not null and exists (
                                 select 1 from platform.job o
                                 where o.unique_key = j.unique_key and o.state = 'ready' and o.id <> j.id)
                               then 'cancelled'
                          else 'ready' end as next_state
              from platform.job j
              where j.id = @id and j.locked_by = @worker and j.state = 'running'
            )
            update platform.job j
               set state = d.next_state,
                   run_after = case when d.next_state = 'ready' then now() + @backoff else j.run_after end,
                   last_error = @error,
                   locked_by = null, locked_at = null, lock_expires_at = null,
                   completed_at = case when d.next_state = 'ready' then null else now() end
              from decided d
             where j.id = d.id
            returning j.state
            """);
        command.Parameters.Add(new NpgsqlParameter("id", jobId));
        command.Parameters.Add(new NpgsqlParameter("worker", workerId));
        command.Parameters.Add(new NpgsqlParameter("error", Truncate(error)));
        command.Parameters.Add(new NpgsqlParameter("backoff", backoff));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Returns jobs whose worker died (lock expired) to the queue, or cancels them if a newer
    /// request for the same key is already waiting. Returns how many were reaped.
    /// </summary>
    public async Task<int> ReapAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            """
            update platform.job j
               set state = case when j.unique_key is not null and exists (
                                       select 1 from platform.job o
                                       where o.unique_key = j.unique_key and o.state = 'ready' and o.id <> j.id)
                                then 'cancelled' else 'ready' end,
                   completed_at = case when j.unique_key is not null and exists (
                                       select 1 from platform.job o
                                       where o.unique_key = j.unique_key and o.state = 'ready' and o.id <> j.id)
                                then now() else null end,
                   last_error = coalesce(j.last_error, 'lock expired; worker presumed dead'),
                   locked_by = null, locked_at = null, lock_expires_at = null
             where j.state = 'running' and j.lock_expires_at < now()
            """);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kept short: a job error is operational, and a long message is where personal data leaks in.</summary>
    private static string Truncate(string error) => error.Length <= 2000 ? error : error[..2000];
}
