using System.Globalization;
using System.Text.Json;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BrightTime.Infrastructure.Auditing;

/// <summary>What the host knows about the request an audit row belongs to. Hashes only, never raw addresses.</summary>
public interface IAuditRequestInfo
{
    string? RequestId { get; }

    /// <summary>The operation as the host saw it, e.g. <c>POST /sign-in</c>.</summary>
    string? Resource { get; }

    /// <summary>A keyed hash of the client address: enough to correlate, not to recover it.</summary>
    byte[]? IpHash { get; }

    byte[]? UserAgentHash { get; }
}

/// <summary>No request: a job or a test.</summary>
public sealed class NoAuditRequestInfo : IAuditRequestInfo
{
    public static readonly NoAuditRequestInfo Instance = new();

    public string? RequestId => null;

    public string? Resource => null;

    public byte[]? IpHash => null;

    public byte[]? UserAgentHash => null;
}

/// <summary>
/// Writes <c>platform.audit_event</c> (decision D11: one row per operation).
/// </summary>
/// <remarks>
/// <para>
/// Two paths. <see cref="RecordInTransactionAsync"/> writes through a module context's open
/// transaction, so a change and its audit row commit or roll back together: there is no state
/// in which the change happened without a trace. <see cref="Record"/> buffers events that
/// belong to the request rather than to one change (a sign-in, a read), and
/// <see cref="FlushAsync"/> writes them in one transaction before the response starts.
/// </para>
/// <para>
/// The table is append-only twice over: by trigger, and by the application roles having no
/// UPDATE or DELETE on it. Rows go in under the tenant's row-level security like any other.
/// </para>
/// </remarks>
public sealed class AuditTrail(
    ITenantContext tenant,
    IAuditRequestInfo request,
    IClock clock,
    NpgsqlDataSource? dataSource = null) : IAuditWriter
{
    private const string InsertSql =
        """
        insert into platform.audit_event
          (id, occurred_at, tenant_id, actor_id, actor_kind, actor_org_id, action, outcome, subject_kind,
           subject_ids, subject_count, resource, reason, request_id, ip_hash, user_agent_hash, detail)
        values (@id, @at, @tenant, @actor, @kind, @org, @action, @outcome, @subject_kind,
                @subjects, @count, @resource, @reason, @request, @ip, @ua, @detail::jsonb)
        """;

    private readonly List<AuditEvent> _buffer = [];

    public bool HasRecorded { get; private set; }

    public void Record(AuditAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _buffer.Add(Build(access));
        HasRecorded = true;
    }

    /// <summary>Writes one row inside <paramref name="db"/>'s current transaction.</summary>
    public async Task RecordInTransactionAsync(BrightTimeDbContext db, AuditAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An audited change must be recorded inside the change's own transaction.");
        }

        AuditEvent row = Build(access);
        await db.Database.ExecuteSqlRawAsync(InsertSql, Parameters(row), cancellationToken).ConfigureAwait(false);
        HasRecorded = true;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        if (dataSource is null)
        {
            throw new InvalidOperationException("Buffered audit events need a database to flush to.");
        }

        AuditEvent[] rows = [.. _buffer];
        _buffer.Clear();

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (IGrouping<Guid, AuditEvent> byTenant in rows.GroupBy(r => r.TenantId))
        {
            await using (NpgsqlCommand set = new("select set_config(@key, @value, true)", connection, tx))
            {
                set.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
                set.Parameters.Add(new NpgsqlParameter("value", byTenant.Key.ToString("D", CultureInfo.InvariantCulture)));
                await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (AuditEvent row in byTenant)
            {
                await using NpgsqlCommand insert = new(InsertSql, connection, tx);
                insert.Parameters.AddRange(Parameters(row));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEvent Build(AuditAccess access)
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("An audit event needs the request's tenant.");
        }

        Guid[] subjects = [.. access.SubjectIds.Distinct()];
        UserId actor = access.ActorId ?? tenant.ActorId;
        string kind = access.ActorId is not null
            ? AuditActorKind.ToDbValue(ActorKind.User)
            : AuditActorKind.ToDbValue(tenant.ActorKind);

        return new AuditEvent
        {
            Id = UuidV7.NewId(),
            OccurredAt = clock.UtcNow,
            TenantId = tenant.TenantId.Value,
            ActorId = actor.IsNone ? null : actor.Value,
            ActorKind = kind,
            ActorOrgId = access.ActorOrgId?.Value,
            Action = access.Action,
            Outcome = access.Outcome,
            SubjectKind = access.SubjectKind,
            SubjectIds = subjects.Length == 0 ? null : subjects,
            SubjectCount = subjects.Length,
            Resource = access.Resource ?? request.Resource,
            Reason = access.Reason,
            RequestId = request.RequestId,
            IpHash = request.IpHash,
            UserAgentHash = request.UserAgentHash,
            Detail = access.Detail is { Count: > 0 } detail ? JsonSerializer.Serialize(detail) : "{}",
        };
    }

    private static NpgsqlParameter[] Parameters(AuditEvent row) =>
    [
        new("id", row.Id),
        new("at", row.OccurredAt),
        new("tenant", row.TenantId),
        new("actor", row.ActorId is { } actor ? actor : DBNull.Value),
        new("kind", row.ActorKind),
        new("org", row.ActorOrgId is { } org ? org : DBNull.Value),
        new("action", row.Action),
        new("outcome", row.Outcome),
        new("subject_kind", (object?)row.SubjectKind ?? DBNull.Value),
        new("subjects", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)row.SubjectIds ?? DBNull.Value },
        new("count", row.SubjectCount),
        new("resource", (object?)row.Resource ?? DBNull.Value),
        new("reason", (object?)row.Reason ?? DBNull.Value),
        new("request", (object?)row.RequestId ?? DBNull.Value),
        new("ip", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)row.IpHash ?? DBNull.Value },
        new("ua", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)row.UserAgentHash ?? DBNull.Value },
        new("detail", row.Detail),
    ];
}
