using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Directory.Provider;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BrightTime.Modules.Directory.Clearances;

/// <summary>
/// The nightly clearance sweep (D98). A clearance that passed and whose expiry date has gone is
/// marked <c>expired</c>; one that expires within the warning window is noticed once per expiry.
/// Either way, the owners and admins of each organisation it counts at are told, in the inbox and
/// by email. Then the next night's sweep is booked.
/// </summary>
/// <remarks>
/// A county-wide clearance counts at every organisation the person is an active member of, so
/// each of those is told. Nobody loses access here: the sweep keeps the record honest and the
/// people responsible informed.
/// </remarks>
public sealed class ClearanceSweepHandler(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit) : IJobHandler
{
    public static string JobType => "directory.clearance_sweep";

    public const string Lapsed = "directory.clearance.lapsed";

    public const string Kind = "clearance_expiring";

    /// <summary>The hour, in the county's zone, the sweep runs each day.</summary>
    public const int Hour = 6;

    /// <summary>One sweep per tenant waits at a time.</summary>
    public static string KeyFor(TenantId tenant) => $"{JobType}:{tenant.Value:N}";

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await SweepAsync(bookNext: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks lapses and notices expiries; returns how many of each.</summary>
    public Task<(int Warned, int Lapsed)> SweepAsync(bool bookNext, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            // Jobs run in UTC; the county's days are in its own zone.
            TimeZoneInfo zone = await ZoneAsync(ct).ConfigureAwait(false);
            DateOnly today = clock.TodayIn(zone);
            DateOnly horizon = today.AddDays(ClearanceCurrency.DefaultWarningDays);
            string[] passing = [ClearanceStatus.Cleared.ToDbValue(), ClearanceStatus.Conditional.ToDbValue()];
            List<StaffClearance> due = await db.Clearances
                .Where(c => passing.Contains(c.Status) && c.ExpiresOn != null && c.ExpiresOn <= horizon
                            && (c.ExpiresOn < today || c.ExpiryNoticeFor == null || c.ExpiryNoticeFor != c.ExpiresOn))
                .ToListAsync(ct).ConfigureAwait(false);

            int warned = 0;
            int lapsed = 0;
            foreach (StaffClearance c in due)
            {
                DateOnly expires = c.ExpiresOn!.Value;
                string notice;
                if (expires < today)
                {
                    c.Status = ClearanceStatus.Expired.ToDbValue();
                    notice = ClearanceNotice.Lapsed;
                    lapsed++;
                    await audit.RecordInTransactionAsync(db, new AuditAccess
                    {
                        Action = Lapsed,
                        SubjectKind = "user",
                        SubjectIds = [c.UserId],
                        ActorOrgId = c.OrgId is { } o ? new OrganisationId(o) : null,
                        Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = c.ClearanceKind },
                    }, ct).ConfigureAwait(false);
                }
                else
                {
                    notice = ClearanceNotice.Expiring;
                    warned++;
                }

                c.ExpiryNoticeFor = expires;
                await TellAsync(c, expires, notice, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (bookNext)
            {
                await db.EnqueueJobAsync(new JobRequest
                {
                    JobType = JobType,
                    TenantId = tenant.TenantId,
                    UniqueKey = KeyFor(tenant.TenantId),
                    RunAfter = NextRun(clock.UtcNow, zone),
                }, ct).ConfigureAwait(false);
            }

            return (warned, lapsed);
        }, cancellationToken);

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        Guid id = tenant.TenantId.Value;
        string? name = await db.Database.SqlQuery<string>($"select timezone as \"Value\" from platform.tenant where id = {id}")
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return name is not null && TimeZoneInfo.TryFindSystemTimeZoneById(name, out TimeZoneInfo? zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>The next <see cref="Hour"/> o'clock in the county's zone after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        DateTime next = local.Date.AddHours(Hour);
        if (next <= local)
        {
            next = next.AddDays(1);
        }

        // A clock change can skip the hour; then take the moment after it.
        while (zone.IsInvalidTime(next))
        {
            next = next.AddHours(1);
        }

        // In UTC: timestamptz takes no other offset.
        return new DateTimeOffset(next, zone.GetUtcOffset(next)).ToUniversalTime();
    }

    /// <summary>The owners and admins of each organisation the clearance counts at: an inbox notice and an email each.</summary>
    private async Task TellAsync(StaffClearance c, DateOnly expires, string notice, CancellationToken ct)
    {
        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == c.UserId && m.Status == MembershipStatus.Active && (c.OrgId == null || m.OrgId == c.OrgId))
            .Select(m => new { m.Id, m.OrgId })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var at in memberships)
        {
            List<Guid> admins = await db.Memberships.AsNoTracking()
                .Where(m => m.OrgId == at.OrgId && m.Status == MembershipStatus.Active && (m.OrgRole == MemberRoles.Owner || m.OrgRole == MemberRoles.Admin))
                .Select(m => m.UserId)
                .ToListAsync(ct).ConfigureAwait(false);
            string org = await db.Organisations.AsNoTracking().Where(o => o.Id == at.OrgId).Select(o => o.DisplayName).FirstAsync(ct).ConfigureAwait(false);
            foreach (Guid admin in admins)
            {
                ClearanceNotice email = new(admin, at.OrgId, at.Id, c.UserId, c.ClearanceKind, expires, notice);
                await db.PublishAsync(tenant.TenantId, new NotificationRequested(
                    admin, Kind, "notify.clearance." + notice,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["organisation"] = org },
                    $"/provider/orgs/{at.OrgId:D}/staff",
                    email.UniqueKey), ct).ConfigureAwait(false);
                await db.EnqueueJobAsync(new JobRequest
                {
                    JobType = ClearanceNotice.JobType,
                    TenantId = tenant.TenantId,
                    Payload = email,
                    UniqueKey = email.UniqueKey,
                }, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Books each tenant's clearance sweep when the worker starts, for now. A sweep already waiting
/// for its hour absorbs the booking, so a restart doesn't run it twice in a day.
/// </summary>
public sealed partial class ClearanceSweepScheduler(IServiceScopeFactory scopes, NpgsqlDataSource dataSource, ILogger<ClearanceSweepScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (Guid tenant in await TenantsAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await BookAsync(scope.ServiceProvider, new TenantId(tenant), stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One tenant's failure must not stop the others'; the next start tries again.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, tenant, ex.GetType().Name);
            }
        }
    }

    /// <summary>Books the tenant's sweep to run now, unless one is already waiting.</summary>
    public static Task<bool> BookAsync(IServiceProvider services, TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<TenantContext>().SetTenant(tenant, TimeZoneInfo.Utc, Locale.Default);
        DirectoryDbContext db = services.GetRequiredService<DirectoryDbContext>();
        return db.InTransactionAsync(ct => db.EnqueueJobAsync(new JobRequest
        {
            JobType = ClearanceSweepHandler.JobType,
            TenantId = tenant,
            UniqueKey = ClearanceSweepHandler.KeyFor(tenant),
        }, ct), cancellationToken);
    }

    private async Task<List<Guid>> TenantsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand command = new("select distinct tenant_id from platform.tenant_domain", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<Guid> tenants = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            tenants.Add(reader.GetGuid(0));
        }

        return tenants;
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Error, Message = "Booking the clearance sweep failed for tenant {Tenant}: {Exception}.")]
    private static partial void LogFailed(ILogger logger, Guid tenant, string exception);
}

public static class ClearanceJobs
{
    /// <summary>The nightly sweep and its booking at the worker's start. The email job is the mail host's.</summary>
    public static IServiceCollection AddClearanceJobs(this IServiceCollection services)
    {
        services.AddJobHandler<ClearanceSweepHandler>();
        services.AddHostedService<ClearanceSweepScheduler>();
        return services;
    }
}
