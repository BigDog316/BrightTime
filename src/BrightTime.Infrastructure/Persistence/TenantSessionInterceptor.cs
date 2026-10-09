using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BrightTime.Infrastructure.Persistence;

/// <summary>
/// Pushes the tenant and actor into the PostgreSQL session so the row-level
/// security policies in <c>db/schema/99-rls-and-grants.sql</c> can see them.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism matters. <c>set_config(key, value, is_local => true)</c> scopes
/// the setting to the surrounding transaction and unwinds with it. The
/// alternative, a session-level <c>SET</c>, survives the connection's return to
/// the pool and leaks the previous request's tenant into the next one — which
/// is the single worst failure mode this design has, because it is invisible
/// and intermittent.
/// </para>
/// <para>
/// That means every unit of work needs a transaction for the setting to live
/// in. <see cref="BrightTimeDbContext"/> opens one even for reads, which is
/// cheap on PostgreSQL and is the price of the guarantee.
/// </para>
/// </remarks>
public sealed class TenantSessionInterceptor(
    ITenantContext tenantContext,
    ILogger<TenantSessionInterceptor> logger) : DbTransactionInterceptor
{
    public override async ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(
        System.Data.Common.DbConnection connection,
        TransactionEndEventData eventData,
        System.Data.Common.DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<System.Data.Common.DbTransaction> TransactionUsedAsync(
        System.Data.Common.DbConnection connection,
        TransactionEventData eventData,
        System.Data.Common.DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask ApplyAsync(
        System.Data.Common.DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.IsResolved)
        {
            // Resolution runs before anything touches the database. Reaching
            // here means a code path skipped it, and continuing would run the
            // query with a NULL tenant: no rows, no error, no clue why.
            throw new InvalidOperationException(
                "No tenant has been resolved for this unit of work. " +
                "Resolve a tenant before opening a database transaction.");
        }

        System.Data.Common.DbConnection? connection = transaction?.Connection;
        if (connection is null)
        {
            return;
        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "select set_config(@tenant_key, @tenant_value, true), " +
            "       set_config(@actor_key, @actor_value, true)";

        command.Parameters.Add(new NpgsqlParameter("tenant_key", SessionVariables.TenantId));
        command.Parameters.Add(new NpgsqlParameter(
            "tenant_value",
            tenantContext.TenantId.Value.ToString("D", CultureInfo.InvariantCulture)));
        command.Parameters.Add(new NpgsqlParameter("actor_key", SessionVariables.ActorId));
        command.Parameters.Add(new NpgsqlParameter(
            "actor_value",
            tenantContext.HasActor
                ? tenantContext.ActorId.Value.ToString("D", CultureInfo.InvariantCulture)
                : string.Empty));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        logger.TenantSessionApplied(tenantContext.TenantId.Value, tenantContext.HasActor);
    }
}

internal static partial class TenantSessionLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Trace,
        Message = "RLS session set for tenant {TenantId} (authenticated actor: {HasActor}).")]
    public static partial void TenantSessionApplied(this ILogger logger, Guid tenantId, bool hasActor);
}
