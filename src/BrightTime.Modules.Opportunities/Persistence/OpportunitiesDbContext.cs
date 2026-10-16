using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BrightTime.Modules.Opportunities.Persistence;

/// <summary>
/// The Opportunities module's context: listings, schedules, occurrences and the search
/// read model. Mapped to <c>db/schema/40-opportunity.sql</c>, which is the source of truth.
/// </summary>
public sealed class OpportunitiesDbContext(DbContextOptions<OpportunitiesDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "opportunity";

    public const string MigrationsHistoryTable = "__ef_migrations_opportunity";

    public override string Schema => SchemaName;

    public DbSet<ListingRecord> Listings => Set<ListingRecord>();

    public DbSet<ListingSessionRecord> Sessions => Set<ListingSessionRecord>();

    public DbSet<ListingOccurrenceRecord> Occurrences => Set<ListingOccurrenceRecord>();

    public DbSet<ListingSearchRecord> ListingSearch => Set<ListingSearchRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ListingRecord>(b =>
        {
            b.ToTable("listing");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.Property(l => l.Slug).HasColumnType("citext");
            b.Property(l => l.ContactEmail).HasColumnType("citext");
            b.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");

            // Generated stored columns: read back, never written.
            b.Property(l => l.AgeRange)
                .HasComputedColumnSql("int4range((age_min)::integer, (age_max)::integer, '[]'::text)", stored: true);
            b.Property(l => l.GradeRange)
                .HasComputedColumnSql("int4range((grade_min)::integer, (grade_max)::integer, '[]'::text)", stored: true);
            b.Property(l => l.IsFree)
                .HasComputedColumnSql("(cost_model = 'free'::text)", stored: true);

            b.HasIndex(l => new { l.TenantId, l.Slug }).IsUnique();
        });

        modelBuilder.Entity<ListingSessionRecord>(b =>
        {
            b.ToTable("listing_session");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Exdates).HasColumnType("date[]");
        });

        modelBuilder.Entity<ListingOccurrenceRecord>(b =>
        {
            b.ToTable("listing_occurrence");
            b.HasKey(o => o.Id);
            b.Property(o => o.Id).UseIdentityAlwaysColumn();
        });

        modelBuilder.Entity<ListingSearchRecord>(b =>
        {
            b.ToTable("listing_search");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.Facets).HasColumnType("text[]");
            b.Property(r => r.Location).HasColumnType("geography (Point, 4326)");
            b.Property(r => r.Popularity).HasColumnType("real");

            // Database-filled: omitted on insert so the default applies, then set by SQL.
            b.Property(r => r.SearchVector)
                .IsRequired()
                .ValueGeneratedOnAdd()
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
