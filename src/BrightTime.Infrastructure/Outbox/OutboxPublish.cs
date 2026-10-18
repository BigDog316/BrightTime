using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrightTime.Infrastructure.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Infrastructure.Outbox;

/// <summary>Publishes module messages into <c>platform.outbox_message</c>, in the caller's own transaction.</summary>
public static class OutboxPublish
{
    /// <summary>How messages are written and read: camel-case names, enums by name, so a reordered enum cannot change a stored message's meaning.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes <paramref name="message"/> through <paramref name="db"/>'s connection and current
    /// transaction, so it exists only if the change that caused it commits: no change without its
    /// message, no message without its change. The dispatcher delivers it to each subscriber.
    /// </summary>
    /// <remarks>
    /// The application role may publish only for its own tenant: the tenant policy on
    /// <c>platform.outbox_message</c> refuses any other. Must run inside a transaction; module
    /// contexts refuse otherwise. A message carries what its subscribers need to know and no
    /// more: it is kept until the retention job removes processed rows.
    /// </remarks>
    public static async Task PublishAsync<TMessage>(
        this BrightTimeDbContext db,
        TenantId tenant,
        TMessage message,
        CancellationToken cancellationToken = default)
        where TMessage : IModuleMessage
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(message);

        Guid messageId = UuidV7.NewId();
        Guid tenantId = tenant.Value;
        string type = TMessage.MessageType;
        string module = SourceModule(type);
        string payload = JsonSerializer.Serialize(message, Json);
        string? trace = Activity.Current?.Id;

        await db.Database.ExecuteSqlAsync(
            $"""
            insert into platform.outbox_message (message_id, tenant_id, source_module, message_type, payload, trace_context)
            values ({messageId}, {tenantId}, {module}, {type}, {payload}::jsonb, {trace})
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The module a message type names first: <c>directory.site.changed</c> is Directory's.</summary>
    public static string SourceModule(string messageType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        int dot = messageType.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? messageType[..dot] : messageType;
    }
}
