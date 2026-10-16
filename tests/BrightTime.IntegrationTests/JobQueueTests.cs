using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>The Postgres job queue: debounce, claiming, retry, reaping, ownership and tenancy.</summary>
[Collection(nameof(DatabaseCollection))]
public sealed class JobQueueTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task A_repeated_request_collapses_into_the_waiting_job()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, string key) = await SetupAsync();

        (bool first, bool second) = await EnqueueTwiceAsync(tenant, queue, key);

        first.ShouldBeTrue();
        second.ShouldBeFalse("a second request while one waits must collapse into it");
        (await CountAsync(key, "ready")).ShouldBe(1);
    }

    [Fact]
    public async Task An_edit_while_a_job_runs_queues_a_fresh_job_and_is_not_lost()
    {
        // The running job may already have read the old state. Collapsing the new request into
        // it would drop the edit. The debounce index covers waiting jobs only.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, string key) = await SetupAsync();

        await EnqueueAsync(tenant, queue, key);
        await using ServiceProvider worker = TestHost.Worker(Cs, queue);
        (await worker.GetRequiredService<JobStore>().ClaimAsync(queue, "w1", 10, TimeSpan.FromMinutes(5), Ct))
            .Count.ShouldBe(1);

        bool queued = await EnqueueAsync(tenant, queue, key);

        queued.ShouldBeTrue();
        (await CountAsync(key, "running")).ShouldBe(1);
        (await CountAsync(key, "ready")).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_job()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, _) = await SetupAsync();

        for (int i = 0; i < 8; i++)
        {
            await EnqueueAsync(tenant, queue, uniqueKey: null);
        }

        await using ServiceProvider worker = TestHost.Worker(Cs, queue);
        JobStore store = worker.GetRequiredService<JobStore>();

        IReadOnlyList<JobRecord>[] claims = await Task.WhenAll(
            store.ClaimAsync(queue, "a", 8, TimeSpan.FromMinutes(5), Ct),
            store.ClaimAsync(queue, "b", 8, TimeSpan.FromMinutes(5), Ct));

        List<Guid> all = [.. claims.SelectMany(c => c).Select(j => j.Id)];
        all.Count.ShouldBe(8, "every job is claimed exactly once");
        all.Distinct().Count().ShouldBe(8, "no job is claimed by both workers");
    }

    [Fact]
    public async Task A_failing_job_retries_with_backoff_then_goes_dead()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, string key) = await SetupAsync();

        await EnqueueAsync(tenant, queue, key, FailingHandler.Type, maxAttempts: 2);
        await using ServiceProvider worker = TestHost.Worker(Cs, queue, s => s.AddJobHandler<FailingHandler>());
        JobRunner runner = worker.GetRequiredService<JobRunner>();

        await runner.RunOnceAsync(Ct);

        (string state, short attempts, double secondsUntilRetry, string? error) = await ReadJobAsync(key);
        state.ShouldBe("ready");
        attempts.ShouldBe((short)1);
        secondsUntilRetry.ShouldBeInRange(3, 6, "first retry backs off about five seconds");
        error.ShouldNotBeNull();
        error.ShouldContain("deliberate failure");

        await MakeDueAsync(key);
        await runner.RunOnceAsync(Ct);

        (state, attempts, _, _) = await ReadJobAsync(key);
        state.ShouldBe("dead", "attempts are spent");
        attempts.ShouldBe((short)2);
    }

    [Fact]
    public async Task A_job_whose_worker_died_goes_back_to_the_queue()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, string key) = await SetupAsync();

        await EnqueueAsync(tenant, queue, key);
        await using ServiceProvider worker = TestHost.Worker(Cs, queue);
        JobStore store = worker.GetRequiredService<JobStore>();
        await store.ClaimAsync(queue, "crashed", 10, TimeSpan.FromMinutes(5), Ct);
        await ExpireLockAsync(key);

        int reaped = await store.ReapAsync(Ct);

        reaped.ShouldBe(1);
        (await CountAsync(key, "ready")).ShouldBe(1);
    }

    [Fact]
    public async Task A_worker_that_lost_its_lock_cannot_complete_the_job()
    {
        // Worker A stalls past its lock; the job is reaped and claimed by B. A waking up and
        // marking it done would hide B's work, so A's completion must be refused.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, string key) = await SetupAsync();

        await EnqueueAsync(tenant, queue, key);
        await using ServiceProvider worker = TestHost.Worker(Cs, queue);
        JobStore store = worker.GetRequiredService<JobStore>();

        JobRecord job = (await store.ClaimAsync(queue, "A", 1, TimeSpan.FromMinutes(5), Ct))[0];
        await ExpireLockAsync(key);
        await store.ReapAsync(Ct);
        await store.ClaimAsync(queue, "B", 1, TimeSpan.FromMinutes(5), Ct);

        bool completedByA = await store.CompleteAsync(job.Id, "A", Ct);

        completedByA.ShouldBeFalse();
        (await CountAsync(key, "running")).ShouldBe(1);
    }

    [Fact]
    public async Task A_handler_runs_with_its_jobs_tenant_set_as_a_job_actor()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, string key) = await SetupAsync();

        await EnqueueAsync(tenant, queue, key, RecordingHandler.Type);
        await using ServiceProvider worker = TestHost.Worker(Cs, queue, s => s.AddJobHandler<RecordingHandler>());
        RecordingHandler.Seen.Clear();

        await worker.GetRequiredService<JobRunner>().RunOnceAsync(Ct);

        RecordingHandler.Seen.ShouldContain((tenant, ActorKind.Job));
        (await CountAsync(key, "succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task A_request_cannot_enqueue_work_for_another_tenant()
    {
        // The application role is tenant-scoped on platform.job; only the worker sees across tenants.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (Guid tenant, string queue, _) = await SetupAsync();
        (Guid other, _, _) = await SetupAsync();

        await using AsyncServiceScope scope = TestHost.AppScope(Cs, tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            db.InTransactionAsync(ct => db.EnqueueJobAsync(new JobRequest
            {
                JobType = RecordingHandler.Type,
                Queue = queue,
                TenantId = new TenantId(other),
            }, ct), Ct));

        error.SqlState.ShouldBe("42501");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<(Guid Tenant, string Queue, string Key)> SetupAsync()
    {
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "jobs-" + tenant.ToString("N"));
        return (tenant, "test-" + UuidV7.NewId().ToString("N"), "key-" + UuidV7.NewId().ToString("N"));
    }

    private async Task<bool> EnqueueAsync(
        Guid tenant, string queue, string? uniqueKey, string? jobType = null, short maxAttempts = 10)
    {
        await using AsyncServiceScope scope = TestHost.AppScope(Cs, tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        return await db.InTransactionAsync(ct => db.EnqueueJobAsync(new JobRequest
        {
            JobType = jobType ?? RecordingHandler.Type,
            Queue = queue,
            TenantId = new TenantId(tenant),
            UniqueKey = uniqueKey,
            MaxAttempts = maxAttempts,
        }, ct), Ct);
    }

    private async Task<(bool, bool)> EnqueueTwiceAsync(Guid tenant, string queue, string key) =>
        (await EnqueueAsync(tenant, queue, key), await EnqueueAsync(tenant, queue, key));

    private async Task<long> CountAsync(string key, string state)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        return await Sql.ScalarAsync<long>(connection, null,
            "select count(*) from platform.job where unique_key = @k and state = @s", [("k", key), ("s", state)]);
    }

    private async Task<(string State, short Attempts, double SecondsUntilRetry, string? Error)> ReadJobAsync(string key)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await using NpgsqlCommand command = new(
            "select state, attempts, extract(epoch from run_after - now())::float8, last_error " +
            "from platform.job where unique_key = @k", connection);
        command.Parameters.Add(new NpgsqlParameter("k", key));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return (reader.GetString(0), reader.GetInt16(1), reader.GetDouble(2),
            await reader.IsDBNullAsync(3, Ct) ? null : reader.GetString(3));
    }

    private async Task MakeDueAsync(string key)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.ExecAsync(connection, null, "update platform.job set run_after = now() where unique_key = @k", [("k", key)]);
    }

    private async Task ExpireLockAsync(string key)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.ExecAsync(connection, null,
            "update platform.job set lock_expires_at = now() - interval '1 second' where unique_key = @k and state = 'running'",
            [("k", key)]);
    }
}

/// <summary>Records the tenant and actor it ran under.</summary>
internal sealed class RecordingHandler(ITenantContext tenant) : IJobHandler
{
    public const string Type = "test.record";

    public static string JobType => Type;

    public static System.Collections.Concurrent.ConcurrentBag<(Guid, ActorKind)> Seen { get; } = [];

    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        Seen.Add((tenant.TenantId.Value, tenant.ActorKind));
        return Task.CompletedTask;
    }
}

/// <summary>Always fails, to exercise retry and dead-lettering.</summary>
internal sealed class FailingHandler : IJobHandler
{
    public const string Type = "test.fail";

    public static string JobType => Type;

    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("deliberate failure");
}
