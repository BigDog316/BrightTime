using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Domain;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BrightTime.Modules.Identity.Consent;

/// <summary>
/// When the terms change (D37), the young people who could sign up for programs themselves under
/// the old ones must agree again first. Their stored capabilities don't know: capabilities change
/// only when recomputed. So, on the worker's start, each tenant's stale rows get a recompute.
/// </summary>
/// <remarks>
/// Stale means a row that allows self-enrolment where there is no agreement to the current terms'
/// hash. After a deployment that changes nothing, that set is empty and nothing is booked. Each
/// booking is keyed by the account and the terms' hash, so a restart books nothing twice.
/// </remarks>
public sealed partial class TermsSweep(IServiceScopeFactory scopes, NpgsqlDataSource dataSource, ILogger<TermsSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (Guid tenant in await TenantsAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                int booked = await SweepAsync(scope.ServiceProvider, new TenantId(tenant), stoppingToken).ConfigureAwait(false);
                if (booked > 0)
                {
                    LogBooked(logger, booked, tenant);
                }
            }
#pragma warning disable CA1031 // One tenant's failure must not stop the others'; the next start tries again.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, tenant, ex.GetType().Name);
            }
        }
    }

    /// <summary>Books a recompute for each of the tenant's stale rows. Returns how many it found.</summary>
    public static async Task<int> SweepAsync(IServiceProvider services, TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<TenantContext>().SetTenant(tenant, TimeZoneInfo.Utc, Locale.Default);
        IdentityDbContext db = services.GetRequiredService<IdentityDbContext>();
        byte[] hash = AccountTerms.Current.Hash;
        string version = Convert.ToHexStringLower(hash)[..12];

        return await db.InTransactionAsync(async ct =>
        {
            List<Guid> stale = await db.YouthCapabilities.AsNoTracking()
                .Where(c => c.CanSelfEnroll
                            && !db.ConsentRecords.Any(r => r.UserId == c.UserId && r.DocumentKind == ConsentDocumentKind.Terms && r.DocumentHash == hash))
                .Select(c => c.UserId)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (Guid user in stale)
            {
                await db.EnqueueJobAsync(new JobRequest
                {
                    JobType = RecomputeCapabilitiesHandler.JobType,
                    TenantId = tenant,
                    Payload = new RecomputeCapabilitiesHandler.Payload(user),
                    UniqueKey = $"{RecomputeCapabilitiesHandler.JobType}:{user:N}:terms-{version}",
                }, ct).ConfigureAwait(false);
            }

            return stale.Count;
        }, cancellationToken).ConfigureAwait(false);
    }

    // Every tenant a host can reach has a domain; the table is readable before any tenant is set.
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

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "The terms changed: booked {Count} capability recomputes for tenant {Tenant}.")]
    private static partial void LogBooked(ILogger logger, int count, Guid tenant);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Error, Message = "The terms sweep failed for tenant {Tenant}: {Exception}.")]
    private static partial void LogFailed(ILogger logger, Guid tenant, string exception);
}
