using BrightTime.Infrastructure.Jobs;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BrightTime.Infrastructure.Outbox;

/// <summary>How the dispatcher polls.</summary>
public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 100;

    /// <summary>How long to wait when nothing is waiting. Messages found means no wait at all.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The job queue deliveries go on.</summary>
    public string Queue { get; set; } = "default";
}

/// <summary>
/// Moves module messages from <c>platform.outbox_message</c> to their subscribers: each message
/// becomes one <see cref="OutboxDeliveryHandler"/> job per handler, written in the same
/// transaction that marks the message processed.
/// </summary>
/// <remarks>
/// <para>
/// Fanning out to jobs rather than calling handlers here is what makes delivery safe. Each
/// handler gets its own retries, backoff and dead letter (the job runner's), so one failing
/// handler neither blocks the others nor makes them run twice. A crash before the commit
/// leaves the message unprocessed and the jobs unwritten, and the next pass does both; a crash
/// after it leaves the jobs to the runner. Delivery is at least once, so handlers are idempotent.
/// </para>
/// <para>
/// Connects as the jobs login. The <c>outbox_dispatch</c> policy lets that role see every
/// tenant's messages, as <c>job_worker_claims</c> does for jobs; the application role stays
/// tenant-scoped and can only publish. Concurrent dispatchers claim with
/// <c>FOR UPDATE SKIP LOCKED</c> and never take the same message. Messages are dispatched in
/// the order they were written, but their deliveries run in any order: handlers read current
/// state rather than relying on order.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    ModuleMessageRegistry registry,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int dispatched;
            try
            {
                dispatched = await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A database outage must not end the dispatcher; it retries on the next poll.
                LoopFailed(logger, ex);
                dispatched = 0;
            }

            if (dispatched == 0)
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Dispatches one batch. Returns how many messages it took.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        List<(long Id, Guid MessageId, Guid TenantId, string Type)> messages = [];
        await using (NpgsqlCommand claim = new(
            """
            select id, message_id, tenant_id, message_type
              from platform.outbox_message
             where processed_at is null
             order by id
             for update skip locked
             limit @batch
            """, connection, tx))
        {
            claim.Parameters.Add(new NpgsqlParameter("batch", _options.BatchSize));
            await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3)));
            }
        }

        if (messages.Count == 0)
        {
            return 0;
        }

        int deliveries = 0;
        await using (NpgsqlBatch batch = new(connection, tx))
        {
            foreach ((_, Guid messageId, Guid tenantId, string type) in messages)
            {
                foreach (string handler in registry.HandlersFor(type))
                {
                    NpgsqlBatchCommand insert = new(
                        """
                        insert into platform.job (id, tenant_id, queue, job_type, payload, unique_key)
                        values (@id, @tenant, @queue, @type, @payload::jsonb, @key)
                        on conflict (unique_key) where unique_key is not null and state = 'ready' do nothing
                        """);
                    insert.Parameters.Add(new NpgsqlParameter("id", UuidV7.NewId()));
                    insert.Parameters.Add(new NpgsqlParameter("tenant", tenantId));
                    insert.Parameters.Add(new NpgsqlParameter("queue", _options.Queue));
                    insert.Parameters.Add(new NpgsqlParameter("type", OutboxDeliveryHandler.JobType));
                    insert.Parameters.Add(new NpgsqlParameter("payload", JobExecutionContext.WritePayload(new OutboxDeliveryHandler.Payload(messageId, handler))));
                    insert.Parameters.Add(new NpgsqlParameter("key", OutboxDeliveryHandler.UniqueKey(messageId, handler)));
                    batch.BatchCommands.Add(insert);
                    deliveries++;
                }
            }

            NpgsqlBatchCommand done = new(
                "update platform.outbox_message set processed_at = now(), attempts = attempts + 1 where id = any(@ids)");
            done.Parameters.Add(new NpgsqlParameter("ids", messages.Select(m => m.Id).ToArray()));
            batch.BatchCommands.Add(done);

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Dispatched(logger, messages.Count, deliveries);
        return messages.Count;
    }

    /// <summary>Dispatches until nothing is waiting. For tests and for draining on demand.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        int total = 0;
        for (int round = 0; round < 100; round++)
        {
            int dispatched = await DispatchOnceAsync(cancellationToken).ConfigureAwait(false);
            if (dispatched == 0)
            {
                break;
            }

            total += dispatched;
        }

        return total;
    }

    /// <summary>A message's type and payload, for its delivery.</summary>
    internal async Task<(Guid TenantId, string Type, string Payload)?> LoadAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "select tenant_id, message_type, payload::text from platform.outbox_message where message_id = @id");
        command.Parameters.Add(new NpgsqlParameter("id", messageId));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Debug, Message = "Dispatched {Messages} outbox messages as {Deliveries} deliveries.")]
    private static partial void Dispatched(ILogger logger, int messages, int deliveries);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "The outbox dispatcher's pass failed; it will try again.")]
    private static partial void LoopFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Delivers one stored module message to one handler. Queued by <see cref="OutboxDispatcher"/>;
/// the payload names the message and the handler, never the message's content.
/// </summary>
public sealed class OutboxDeliveryHandler(OutboxDispatcher outbox, ModuleMessageRegistry registry, IServiceProvider scope) : IJobHandler
{
    public static string JobType => "outbox.deliver";

    public sealed record Payload(Guid MessageId, string Handler);

    public static string UniqueKey(Guid messageId, string handler) => $"{JobType}:{messageId:N}:{handler}";

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Payload payload = context.ReadPayload<Payload>();

        (Guid tenant, string type, string body) = await outbox.LoadAsync(payload.MessageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Outbox message {payload.MessageId} no longer exists.");

        // The runner set the job's tenant on this scope; the message must be that tenant's.
        if (context.TenantId?.Value != tenant)
        {
            throw new InvalidOperationException($"Outbox message {payload.MessageId} belongs to another tenant than its delivery.");
        }

        ModuleMessageRegistry.Invoker invoke = registry.Resolve(payload.Handler)
            ?? throw new InvalidOperationException($"No handler {payload.Handler} is registered for {type}.");
        await invoke(scope, body, cancellationToken).ConfigureAwait(false);
    }
}

public static class OutboxRegistration
{
    /// <summary>
    /// The dispatcher, its delivery job, and the registry handlers are added to with
    /// <see cref="ModuleMessageRegistration.AddMessageHandler{TMessage, THandler}"/>. Needs the
    /// jobs login's <see cref="NpgsqlDataSource"/> and the job runner.
    /// </summary>
    public static IServiceCollection AddOutboxDispatch(this IServiceCollection services, bool runInBackground = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ModuleMessageRegistration.Registry(services);
        services.AddOptions<OutboxOptions>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddJobHandler<OutboxDeliveryHandler>();
        if (runInBackground)
        {
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        }

        return services;
    }
}
