using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BrightTime.Infrastructure.Persistence;

/// <summary>
/// Refuses any command a module context runs outside a transaction.
/// </summary>
/// <remarks>
/// <para>
/// The tenant and actor reach PostgreSQL through <c>set_config(..., is_local =&gt; true)</c>,
/// which only exists inside a transaction. A query run outside one has no tenant. Row-level
/// security then returns no rows at all: no error, just an empty page and a confused
/// family. This interceptor turns that silent failure into a loud one at the call site.
/// </para>
/// <para>
/// Writes are covered by <c>AutoTransactionBehavior.Always</c> on
/// <see cref="BrightTimeDbContext"/>. Reads go through
/// <see cref="BrightTimeDbContext.InTransactionAsync{T}"/>.
/// </para>
/// </remarks>
public sealed class RequireTransactionInterceptor : DbCommandInterceptor
{
    public static readonly RequireTransactionInterceptor Instance = new();

    private RequireTransactionInterceptor()
    {
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Require(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Require(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Require(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Require(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Require(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Require(command);
        return ValueTask.FromResult(result);
    }

    private static void Require(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Transaction is null)
        {
            throw new InvalidOperationException(
                "A module context ran a command outside a transaction, so no tenant was set and " +
                "row-level security would have returned nothing. Wrap reads in InTransactionAsync.");
        }
    }
}
