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

    public DbSet<ListingSiteRecord> ListingSites => Set<ListingSiteRecord>();

    public DbSet<ListingTermRecord> ListingTerms => Set<ListingTermRecord>();

    public DbSet<ListingLanguageRecord> ListingLanguages => Set<ListingLanguageRecord>();

    public DbSet<ListingAccommodationRecord> ListingAccommodations => Set<ListingAccommodationRecord>();

    public DbSet<ListingDeadlineRecord> ListingDeadlines => Set<ListingDeadlineRecord>();

    public DbSet<ListingTranslationRecord> ListingTranslations => Set<ListingTranslationRecord>();

    public DbSet<ListingRevisionRecord> ListingRevisions => Set<ListingRevisionRecord>();

    public DbSet<CollectionRecord> Collections => Set<CollectionRecord>();

    public DbSet<CollectionItemRecord> CollectionItems => Set<CollectionItemRecord>();

    public DbSet<CollectionTranslationRecord> CollectionTranslations => Set<CollectionTranslationRecord>();

    public DbSet<NeighbourhoodPageRecord> NeighbourhoodPages => Set<NeighbourhoodPageRecord>();

    public DbSet<NeighbourhoodPageBlockRecord> NeighbourhoodPageBlocks => Set<NeighbourhoodPageBlockRecord>();

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
            b.Property(r => r.Slug).HasColumnType("citext");
            b.Property(r => r.Location).HasColumnType("geography (Point, 4326)");
            b.Property(r => r.Popularity).HasColumnType("real");

            // Database-filled: omitted on insert so the default applies, then set by SQL.
            b.Property(r => r.SearchVector)
                .IsRequired()
                .ValueGeneratedOnAdd()
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        });

        modelBuilder.Entity<ListingRevisionRecord>(b =>
        {
            b.ToTable("listing_revision");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.Snapshot).HasColumnType("jsonb");
            b.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ListingSiteRecord>(b =>
        {
            b.ToTable("listing_site");
            b.HasKey(x => new { x.ListingId, x.SiteId });
        });

        modelBuilder.Entity<ListingTermRecord>(b =>
        {
            b.ToTable("listing_term");
            b.HasKey(x => new { x.ListingId, x.TermId });
        });

        modelBuilder.Entity<ListingLanguageRecord>(b =>
        {
            b.ToTable("listing_language");
            b.HasKey(x => new { x.ListingId, x.LanguageCode });
        });

        modelBuilder.Entity<ListingAccommodationRecord>(b =>
        {
            b.ToTable("listing_accommodation");
            b.HasKey(x => new { x.ListingId, x.TermId });
        });

        modelBuilder.Entity<ListingDeadlineRecord>(b =>
        {
            b.ToTable("listing_deadline");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<ListingTranslationRecord>(b =>
        {
            b.ToTable("listing_translation");
            b.HasKey(x => new { x.ListingId, x.Locale });
            b.Property(x => x.TranslatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CollectionRecord>(b =>
        {
            b.ToTable("collection");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Slug).HasColumnType("citext");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CollectionItemRecord>(b =>
        {
            b.ToTable("collection_item");
            b.HasKey(x => new { x.CollectionId, x.ListingId });
        });

        modelBuilder.Entity<CollectionTranslationRecord>(b =>
        {
            b.ToTable("collection_translation");
            b.HasKey(x => new { x.CollectionId, x.Locale });
        });

        modelBuilder.Entity<NeighbourhoodPageRecord>(b =>
        {
            b.ToTable("neighbourhood_page");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<NeighbourhoodPageBlockRecord>(b =>
        {
            b.ToTable("neighbourhood_page_block");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
