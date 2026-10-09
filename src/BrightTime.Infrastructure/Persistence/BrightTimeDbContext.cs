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
public abstract class BrightTimeDbContext(DbContextOptions options, ITenantContext tenantContext)
    : DbContext(options)
{
    /// <summary>The PostgreSQL schema this module owns.</summary>
    public abstract string Schema { get; }

    protected ITenantContext TenantContext { get; } = tenantContext;

    protected TenantId TenantId => TenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // snake_case is the schema's convention; mapping it once here beats
        // 177 tables of explicit HasColumnName.
        configurationBuilder.Properties<string>().HaveMaxLength(DefaultStringLength);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateOnly>().HaveColumnType("date");

        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>
    /// Deliberately generous: the schema uses unbounded <c>text</c> throughout
    /// (PostgreSQL stores it identically to a capped varchar), and real limits
    /// are expressed as CHECK constraints where they matter.
    /// </summary>
    protected const int DefaultStringLength = 4000;
}
