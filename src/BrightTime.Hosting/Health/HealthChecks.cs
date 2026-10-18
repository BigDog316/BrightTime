using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace BrightTime.Hosting.Health;

/// <summary>When a queue's lag counts as degraded.</summary>
public sealed class HealthOptions
{
    public const string SectionName = "Health";

    /// <summary>Oldest undispatched outbox message. The dispatcher polls every second, so a minute is a stuck dispatcher.</summary>
    public TimeSpan OutboxLagDegraded { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Oldest job that is due but not yet claimed.</summary>
    public TimeSpan JobLagDegraded { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>The database answers, as the host's own login.</summary>
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("select 1");
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException)
        {
            // No exception text in the result: the probe's answer is public to whoever can reach it.
            return HealthCheckResult.Unhealthy("The database did not answer.");
        }
    }
}

/// <summary>
/// The outbox and job queues are moving. Lag is reported as degraded, never unhealthy: a stuck
/// worker is the jobs host's fault, and taking this host out of rotation would not fix it.
/// The jobs host has no HTTP surface of its own, so this is how its health is seen.
/// </summary>
public sealed class QueueHealthCheck(NpgsqlDataSource dataSource, Microsoft.Extensions.Options.IOptions<HealthOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("select * from platform.queue_health()");
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            long outboxPending = reader.GetInt64(0);
            double outboxOldest = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetDouble(1);
            long jobsDue = reader.GetInt64(2);
            double jobsOldest = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetDouble(3);
            long jobsDead = reader.GetInt64(4);

            Dictionary<string, object> data = new(StringComparer.Ordinal)
            {
                ["outboxPending"] = outboxPending,
                ["outboxOldestSeconds"] = Math.Round(outboxOldest, 1),
                ["jobsDue"] = jobsDue,
                ["jobsOldestDueSeconds"] = Math.Round(jobsOldest, 1),
                ["jobsDead"] = jobsDead,
            };

            List<string> problems = [];
            if (outboxOldest > options.Value.OutboxLagDegraded.TotalSeconds)
            {
                problems.Add("the outbox is not being dispatched");
            }

            if (jobsOldest > options.Value.JobLagDegraded.TotalSeconds)
            {
                problems.Add("due jobs are waiting");
            }

            if (jobsDead > 0)
            {
                problems.Add("jobs have failed for good");
            }

            return problems.Count == 0
                ? HealthCheckResult.Healthy(data: data)
                : HealthCheckResult.Degraded(string.Join("; ", problems), data: data);
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Degraded("The queues could not be read.");
        }
    }
}

public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    private const string ReadyTag = "ready";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Liveness, and readiness: the database (and, with <paramref name="queues"/>, the outbox and
    /// job lag). Without a database the host is ready when it is alive.
    /// </summary>
    public static IServiceCollection AddBrightTimeHealth(this IServiceCollection services, IConfiguration configuration, bool hasDatabase, bool queues = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<HealthOptions>(configuration.GetSection(HealthOptions.SectionName));
        IHealthChecksBuilder checks = services.AddHealthChecks();
        if (hasDatabase)
        {
            checks.AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag]);
            if (queues)
            {
                checks.AddCheck<QueueHealthCheck>("queues", tags: [ReadyTag]);
            }
        }

        return services;
    }

    /// <summary>
    /// <c>/health/live</c> runs no checks: the process answering is the whole question.
    /// <c>/health/ready</c> runs the database and queue checks; degraded is still 200, unhealthy is 503.
    /// Both are answered without a tenant (see <see cref="Tenancy.HostTenancy.IsHealthProbe"/>).
    /// </summary>
    public static IEndpointRouteBuilder MapBrightTimeHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync });
        return endpoints;
    }

    /// <summary>Status, and per check its status, description and figures: no exception text, no configuration.</summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        // With a body, so status-code pages do not re-run a 503 as a page.
        if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description, data = e.Value.Data.Count == 0 ? null : e.Value.Data }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted);
    }
}
