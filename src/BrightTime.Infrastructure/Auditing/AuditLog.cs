using System.Globalization;
using System.Text;
using System.Text.Json;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using Npgsql;

namespace BrightTime.Infrastructure.Auditing;

/// <summary>What the explorer narrows by. Every filter is optional; dates are UTC instants.</summary>
public sealed record AuditFilter
{
    public Guid? ActorId { get; init; }

    /// <summary>An action or the start of one: <c>auth.</c> finds every authentication event.</summary>
    public string? ActionPrefix { get; init; }

    public Guid? SubjectId { get; init; }

    public string? Outcome { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Until { get; init; }
}

public sealed record AuditRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorId,
    string ActorKind,
    Guid? ActorOrgId,
    string Action,
    string Outcome,
    string? SubjectKind,
    IReadOnlyList<Guid> SubjectIds,
    int SubjectCount,
    string? Resource,
    string? Reason,
    IReadOnlyDictionary<string, string> Detail);

/// <summary>One page, newest first, and the cursor for the next (older) one if there is more.</summary>
public sealed record AuditPage(IReadOnlyList<AuditRow> Rows, string? Older);

/// <summary>
/// Reads <c>platform.audit_event</c> for one tenant, newest first, by keyset.
/// </summary>
/// <remarks>
/// Keyset rather than offset: the table only grows, so an offset page drifts as rows arrive and
/// costs more the deeper it goes. The cursor is the last row's <c>(occurred_at, id)</c>, which
/// is the primary key, so every partition answers it from an index. Under the tenant policy
/// like any other read; nothing here can see another tenant's trail.
/// </remarks>
public sealed class AuditLog(NpgsqlDataSource dataSource)
{
    public const int PageSize = 50;

    public async Task<AuditPage> SearchAsync(TenantId tenant, UserId reader, AuditFilter filter, string? cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        List<string> where = [];
        List<NpgsqlParameter> parameters = [];
        void Add(string clause, string name, object value)
        {
            where.Add(clause);
            parameters.Add(new NpgsqlParameter(name, value));
        }

        if (filter.ActorId is { } actor)
        {
            Add("actor_id = @actor", "actor", actor);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionPrefix))
        {
            Add("starts_with(action, @action)", "action", filter.ActionPrefix.Trim());
        }

        if (filter.SubjectId is { } subject)
        {
            where.Add("subject_ids @> array[@subject]::uuid[]");
            parameters.Add(new NpgsqlParameter("subject", subject));
        }

        if (filter.Outcome is { Length: > 0 } outcome)
        {
            Add("outcome = @outcome", "outcome", outcome);
        }

        if (filter.From is { } from)
        {
            Add("occurred_at >= @from", "from", from.ToUniversalTime());
        }

        if (filter.Until is { } until)
        {
            Add("occurred_at < @until", "until", until.ToUniversalTime());
        }

        if (Decode(cursor) is { } after)
        {
            where.Add("(occurred_at, id) < (@after_at, @after_id)");
            parameters.Add(new NpgsqlParameter("after_at", after.At));
            parameters.Add(new NpgsqlParameter("after_id", after.Id));
        }

        parameters.Add(new NpgsqlParameter("take", PageSize + 1));
        string sql =
            $"""
             select id, occurred_at, actor_id, actor_kind, actor_org_id, action, outcome, subject_kind,
                    coalesce(subject_ids, array[]::uuid[]), subject_count, resource, reason, detail::text
             from platform.audit_event
             {(where.Count == 0 ? string.Empty : "where " + string.Join(" and ", where))}
             order by occurred_at desc, id desc
             limit @take
             """;

        List<AuditRow> rows = await QueryAsync(tenant, reader, sql, parameters, cancellationToken).ConfigureAwait(false);
        if (rows.Count <= PageSize)
        {
            return new AuditPage(rows, null);
        }

        rows.RemoveAt(PageSize);
        AuditRow last = rows[^1];
        return new AuditPage(rows, Encode(last.OccurredAt, last.Id));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The query is assembled from constant clauses; every value is a bound parameter.")]
    private async Task<List<AuditRow>> QueryAsync(TenantId tenant, UserId reader, string sql, List<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (NpgsqlCommand set = new("select set_config(@tk, @tv, true), set_config(@ak, @av, true)", connection, tx))
        {
            set.Parameters.Add(new NpgsqlParameter("tk", SessionVariables.TenantId));
            set.Parameters.Add(new NpgsqlParameter("tv", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
            set.Parameters.Add(new NpgsqlParameter("ak", SessionVariables.ActorId));
            set.Parameters.Add(new NpgsqlParameter("av", reader.Value.ToString("D", CultureInfo.InvariantCulture)));
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        List<AuditRow> rows = [];
        await using (NpgsqlCommand command = new(sql, connection, tx))
        {
            command.Parameters.AddRange(parameters.ToArray());
            await using NpgsqlDataReader r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new AuditRow(
                    r.GetGuid(0),
                    await r.GetFieldValueAsync<DateTimeOffset>(1, ct).ConfigureAwait(false),
                    await r.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : r.GetGuid(2),
                    r.GetString(3),
                    await r.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : r.GetGuid(4),
                    r.GetString(5),
                    r.GetString(6),
                    await r.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : r.GetString(7),
                    await r.GetFieldValueAsync<Guid[]>(8, ct).ConfigureAwait(false),
                    r.GetInt32(9),
                    await r.IsDBNullAsync(10, ct).ConfigureAwait(false) ? null : r.GetString(10),
                    await r.IsDBNullAsync(11, ct).ConfigureAwait(false) ? null : r.GetString(11),
                    ParseDetail(r.GetString(12))));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private static Dictionary<string, string> ParseDetail(string json)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in document.RootElement.EnumerateObject())
            {
                values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
        }

        return values;
    }

    /// <summary>Opaque to the page: microseconds since the epoch and the row id.</summary>
    public static string Encode(DateTimeOffset at, Guid id)
    {
        long micros = (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"{micros.ToString(CultureInfo.InvariantCulture)}.{id:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static (DateTimeOffset At, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 96)
        {
            return null;
        }

        try
        {
            string padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            string[] parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split('.');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long micros)
                && Guid.TryParseExact(parts[1], "N", out Guid id))
            {
                return (DateTimeOffset.UnixEpoch.AddTicks(micros * 10), id);
            }
        }
        catch (FormatException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return null;
    }
}
