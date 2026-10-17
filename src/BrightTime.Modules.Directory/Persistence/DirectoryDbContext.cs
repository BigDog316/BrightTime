using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Persistence;

/// <summary>The Directory module's context: organisations, vetting history, sites, staff and clearances.</summary>
public sealed class DirectoryDbContext(DbContextOptions<DirectoryDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "directory";

    public const string MigrationsHistoryTable = "__ef_migrations_directory";

    public override string Schema => SchemaName;

    public DbSet<Organisation> Organisations => Set<Organisation>();

    public DbSet<OrganisationVettingEvent> VettingEvents => Set<OrganisationVettingEvent>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<OrgMembership> Memberships => Set<OrgMembership>();

    public DbSet<StaffClearance> Clearances => Set<StaffClearance>();

    public DbSet<SiteHours> SiteHours => Set<SiteHours>();

    public DbSet<SiteAssignment> SiteAssignments => Set<SiteAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Organisation>(b =>
        {
            b.ToTable("organisation");
            b.HasKey(o => o.Id);
            b.Property(o => o.Id).ValueGeneratedNever();
            b.Property(o => o.Slug).HasColumnType("citext");
            b.Property(o => o.PublicEmail).HasColumnType("citext");
            b.Property(o => o.LanguagesServed).HasColumnType("text[]");
            b.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");

            b.HasIndex(o => new { o.TenantId, o.Slug }).IsUnique();
            b.HasIndex(o => new { o.TenantId, o.Status });
            // ix_organisation_review_queue: the organiser's work list.
            b.HasIndex(o => new { o.TenantId, o.SubmittedAt })
             .HasFilter("status in ('applied','under_review','changes_requested')");
        });

        modelBuilder.Entity<OrganisationVettingEvent>(b =>
        {
            b.ToTable("organisation_vetting_event");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Checklist).HasColumnType("jsonb");
            b.HasIndex(e => new { e.OrgId, e.OccurredAt });
        });

        modelBuilder.Entity<Site>(b =>
        {
            b.ToTable("site");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            // geography, not geometry: ST_DWithin on geography is metres on a
            // spheroid, which is what "within 2 km" has to mean.
            b.Property(s => s.Location).HasColumnType("geography (Point, 4326)");
            b.Property(s => s.CountryCode).HasColumnType("char(2)");
            b.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");

            b.HasIndex(s => s.OrgId).HasFilter("is_active");
            b.HasIndex(s => new { s.TenantId, s.NeighbourhoodId }).HasFilter("is_active");
        });

        modelBuilder.Entity<OrgMembership>(b =>
        {
            b.ToTable("org_membership");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).ValueGeneratedNever();
            // ux_org_membership_active: one live membership per person per org.
            b.HasIndex(m => new { m.OrgId, m.UserId })
             .IsUnique()
             .HasFilter("status in ('invited','active','suspended')");
            b.HasIndex(m => m.UserId).HasFilter("status = 'active'");
        });

        modelBuilder.Entity<StaffClearance>(b =>
        {
            b.ToTable("staff_clearance");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            b.HasIndex(c => new { c.TenantId, c.ExpiresOn })
             .HasFilter("status in ('cleared','conditional')");
            b.HasIndex(c => c.UserId);
        });

        modelBuilder.Entity<SiteHours>(b =>
        {
            b.ToTable("site_hours");
            b.HasKey(h => new { h.SiteId, h.DayOfWeek, h.Season, h.OpensAt });
        });

        modelBuilder.Entity<SiteAssignment>(b =>
        {
            b.ToTable("site_assignment");
            b.HasKey(a => new { a.OrgMembershipId, a.SiteId });
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
