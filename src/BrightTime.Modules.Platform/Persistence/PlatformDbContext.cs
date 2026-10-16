using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Persistence;

/// <summary>
/// The Platform module's context: tenancy, localisation, the audit log, the
/// outbox and the job queue.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "platform";

    /// <summary>The migrations history table for this module, so modules migrate independently.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_platform";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<TenantLocale> TenantLocales => Set<TenantLocale>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<TaxonomyTerm> TaxonomyTerms => Set<TaxonomyTerm>();

    public DbSet<TaxonomyTermName> TaxonomyTermNames => Set<TaxonomyTermName>();

    public DbSet<NeighbourhoodName> NeighbourhoodNames => Set<NeighbourhoodName>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("tenant");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).ValueGeneratedNever();
            b.Property(t => t.Branding).HasColumnType("jsonb");
            b.Property(t => t.Slug).HasColumnType("citext");
            // xmin as the concurrency token: no hand-maintained version column.
            b.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");
        });

        modelBuilder.Entity<TenantDomain>(b =>
        {
            b.ToTable("tenant_domain");
            b.HasKey(d => d.Host);
            b.Property(d => d.Host).HasColumnType("citext");
        });

        modelBuilder.Entity<TenantLocale>(b =>
        {
            b.ToTable("tenant_locale");
            b.HasKey(l => new { l.TenantId, l.Locale });
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_event");
            // Composite key: the partition key has to be part of it.
            b.HasKey(e => new { e.Id, e.OccurredAt });
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Detail).HasColumnType("jsonb");
            b.Property(e => e.SubjectIds).HasColumnType("uuid[]");
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_message");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).UseIdentityAlwaysColumn();
            b.Property(m => m.Payload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<JobRecord>(b =>
        {
            b.ToTable("job");
            b.HasKey(j => j.Id);
            b.Property(j => j.Id).ValueGeneratedNever();
            b.Property(j => j.Payload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<TaxonomyTerm>(b =>
        {
            b.ToTable("taxonomy_term");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).ValueGeneratedNever();
            b.Property(t => t.Code).HasColumnType("citext");
        });

        modelBuilder.Entity<TaxonomyTermName>(b =>
        {
            b.ToTable("taxonomy_term_name");
            b.HasKey(n => new { n.TermId, n.Locale });
        });

        modelBuilder.Entity<NeighbourhoodName>(b =>
        {
            b.ToTable("neighbourhood_name");
            b.HasKey(n => new { n.NeighbourhoodId, n.Locale });
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
