using System.Collections.Concurrent;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The outbox end to end: a message written in the publisher's transaction, fanned out by the
/// dispatcher to one job per handler, and delivered by the job runner in the message's tenant.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OutboxTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    public sealed record Ping(Guid Marker, string Note) : IModuleMessage
    {
        public static string MessageType => "test.outbox.ping";
    }

    public sealed record Unheard(Guid Marker) : IModuleMessage
    {
        public static string MessageType => "test.outbox.unheard";
    }

    /// <summary>Records what it was given and the tenant it ran in.</summary>
    public sealed class Recorder(ITenantContext tenant) : IModuleMessageHandler<Ping>
    {
        public static readonly ConcurrentDictionary<Guid, List<(string Note, Guid Tenant)>> Seen = new();

        public Task HandleAsync(Ping message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            Seen.AddOrUpdate(message.Marker, _ => [(message.Note, tenant.TenantId.Value)], (_, l) => [.. l, (message.Note, tenant.TenantId.Value)]);
            return Task.CompletedTask;
        }
    }

    public sealed class Failing : IModuleMessageHandler<Ping>
    {
        public Task HandleAsync(Ping message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This handler always fails.");
    }

    [Fact]
    public async Task A_message_from_a_change_that_rolled_back_was_never_published()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync();
        Guid marker = Guid.NewGuid();

        await using (AsyncServiceScope scope = TestHost.AppScope(Cs, tenant))
        {
            OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
            await Should.ThrowAsync<InvalidOperationException>(() => db.InTransactionAsync<int>(async ct =>
            {
                await db.PublishAsync(new TenantId(tenant), new Ping(marker, "lost"), ct);
                throw new InvalidOperationException("The change failed.");
            }, Ct));
        }

        (await CountAsync("select count(*) from platform.outbox_message where (payload->>'marker')::uuid = @m", marker)).ShouldBe(0);
    }

    [Fact]
    public async Task Each_handler_gets_its_own_delivery_in_the_messages_tenant_and_a_failing_one_stops_no_other()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync();
        Guid marker = Guid.NewGuid();
        await PublishAsync(tenant, new Ping(marker, "hello"));

        string queue = "outbox-" + marker.ToString("N")[..8];
        await using ServiceProvider worker = Worker(queue);
        await TestHost.DrainAsync(worker, Ct);

        Recorder.Seen[marker].ShouldBe([("hello", tenant)]);
        Guid messageId = await ScalarAsync<Guid>("select message_id from platform.outbox_message where (payload->>'marker')::uuid = @m", marker);
        (await ScalarAsync<bool>("select processed_at is not null from platform.outbox_message where message_id = @m", messageId)).ShouldBeTrue();

        string recorder = OutboxDeliveryHandler.UniqueKey(messageId, ModuleMessageRegistry.KeyOf(typeof(Recorder)));
        string failing = OutboxDeliveryHandler.UniqueKey(messageId, ModuleMessageRegistry.KeyOf(typeof(Failing)));
        (await ScalarAsync<string>("select state from platform.job where unique_key = @m", recorder)).ShouldBe("succeeded");
        (await ScalarAsync<string>("select state || ':' || (last_error like '%always fails%') from platform.job where unique_key = @m", failing))
            .ShouldBe("ready:true", "it waits for its retry, and only it does");
        (await ScalarAsync<string>("select payload::text from platform.job where unique_key = @m", recorder)).ShouldNotContain("hello", Case.Sensitive,
            "a delivery names the message; its content stays in the outbox");
    }

    [Fact]
    public async Task A_message_nobody_handles_is_marked_processed_and_queues_nothing()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync();
        Guid marker = Guid.NewGuid();
        await PublishAsync(tenant, new Unheard(marker));

        await using ServiceProvider worker = Worker("outbox-" + marker.ToString("N")[..8]);
        await TestHost.DrainAsync(worker, Ct);

        Guid messageId = await ScalarAsync<Guid>("select message_id from platform.outbox_message where (payload->>'marker')::uuid = @m", marker);
        (await ScalarAsync<bool>("select processed_at is not null from platform.outbox_message where message_id = @m", messageId)).ShouldBeTrue();
        (await CountAsync("select count(*) from platform.job where unique_key like @m", $"{OutboxDeliveryHandler.JobType}:{messageId:N}:%")).ShouldBe(0);
    }

    [Fact]
    public async Task Dispatchers_running_at_once_never_take_the_same_message()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid tenant = await TenantAsync();
        Guid batch = Guid.NewGuid();
        await using (AsyncServiceScope scope = TestHost.AppScope(Cs, tenant))
        {
            OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                for (int i = 0; i < 60; i++)
                {
                    await db.PublishAsync(new TenantId(tenant), new Ping(batch, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);
                }

                return 0;
            }, Ct);
        }

        await using ServiceProvider first = Worker("outbox-a-" + batch.ToString("N")[..6], batchSize: 25);
        await using ServiceProvider second = Worker("outbox-b-" + batch.ToString("N")[..6], batchSize: 25);
        await Task.WhenAll(
            first.GetRequiredService<OutboxDispatcher>().DrainAsync(Ct),
            second.GetRequiredService<OutboxDispatcher>().DrainAsync(Ct));

        // Two handlers per message: 120 deliveries, none twice.
        (await CountAsync(
            """
            select count(*) from platform.job j
             where j.job_type = 'outbox.deliver'
               and (j.payload->>'messageId')::uuid in (select message_id from platform.outbox_message where (payload->>'marker')::uuid = @m)
            """, batch)).ShouldBe(120);
        (await CountAsync("select count(*) from platform.outbox_message where (payload->>'marker')::uuid = @m and attempts <> 1", batch)).ShouldBe(0);
    }

    [Fact]
    public async Task The_application_cannot_publish_for_another_tenant()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        Guid mine = await TenantAsync();
        Guid theirs = await TenantAsync();

        await using AsyncServiceScope scope = TestHost.AppScope(Cs, mine);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        PostgresException refused = await Should.ThrowAsync<PostgresException>(() =>
            db.InTransactionAsync(async ct =>
            {
                await db.PublishAsync(new TenantId(theirs), new Ping(Guid.NewGuid(), "forged"), ct);
                return 0;
            }, Ct));
        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ---- helpers -------------------------------------------------------------------------

    private ServiceProvider Worker(string queue, int batchSize = 100) =>
        TestHost.Worker(Cs, queue, s =>
        {
            s.Configure<OutboxOptions>(o =>
            {
                o.Queue = queue;
                o.BatchSize = batchSize;
            });
            s.AddMessageHandler<Ping, Recorder>();
            s.AddMessageHandler<Ping, Failing>();
        });

    private async Task<Guid> TenantAsync()
    {
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "outbox-" + tenant.ToString("N"));
        return tenant;
    }

    private async Task PublishAsync<TMessage>(Guid tenant, TMessage message) where TMessage : IModuleMessage
    {
        await using AsyncServiceScope scope = TestHost.AppScope(Cs, tenant);
        OpportunitiesDbContext db = scope.ServiceProvider.GetRequiredService<OpportunitiesDbContext>();
        await db.InTransactionAsync(async ct =>
        {
            await db.PublishAsync(new TenantId(tenant), message, ct);
            return 0;
        }, Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql, object value)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        return await Sql.ScalarAsync<T>(connection, null, sql, [("m", value)]);
    }

    private Task<long> CountAsync(string sql, object value) => ScalarAsync<long>(sql, value);
}
