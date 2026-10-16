using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Infrastructure.Persistence;

/// <summary>
/// Base class for every module's <see cref="DbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// One context per module, each pinned to its own schema and its own
/// migrations history table, so modules migrate independently and a module can
/// later be lifted out without untangling a shared model.
/// </para>
/// <para>
/// <see cref="TenantId"/> is exposed to derived contexts for stamping new
/// entities. It is <em>not</em> a substitute for row-level security: the
/// database enforces isolation whatever the application believes, which is the
/// point of having both.
/// </para>
/// </remarks>
public abstract class BrightTimeDbContext : DbContext
{
    protected BrightTimeDbContext(DbContextOptions options, ITenantContext tenantContext)
        : base(options)
    {
        TenantContext = tenantContext;

        // EF Core skips the transaction for a single-statement SaveChanges. The tenant is
        // set when a transaction starts, so a one-row write would run without it and the
        // RLS policy would refuse it. Every save gets a transaction.
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
    }

    /// <summary>The PostgreSQL schema this module owns.</summary>
    public abstract string Schema { get; }

    protected ITenantContext TenantContext { get; }

    protected TenantId TenantId => TenantContext.TenantId;

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction, which is where the tenant session
    /// variables live. Reads must go through this; <see cref="RequireTransactionInterceptor"/>
    /// refuses a command that does not.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        T result = await work(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // The schema uses unbounded text throughout (PostgreSQL stores it exactly as
        // it stores a capped varchar), with real limits expressed as CHECK
        // constraints. Mapping strings to varchar(n) here put the model and the
        // schema out of step on every string column; the drift test caught it.
        // Columns of another type (citext) declare it explicitly.
        configurationBuilder.Properties<string>().HaveColumnType("text");
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateOnly>().HaveColumnType("date");

        base.ConfigureConventions(configurationBuilder);
    }

}
