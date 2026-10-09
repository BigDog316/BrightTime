using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Identity.Persistence;

/// <summary>
/// The Identity module's context: accounts, roles, credentials, guardianship,
/// consent, capability and youth-access grants.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "identity";

    public const string MigrationsHistoryTable = "__ef_migrations_identity";

    public override string Schema => SchemaName;

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<GuardianLink> GuardianLinks => Set<GuardianLink>();

    public DbSet<ParentalConsent> ParentalConsents => Set<ParentalConsent>();

    public DbSet<YouthCapabilityRecord> YouthCapabilities => Set<YouthCapabilityRecord>();

    public DbSet<RestrictedContact> RestrictedContacts => Set<RestrictedContact>();

    public DbSet<YouthAccessGrant> YouthAccessGrants => Set<YouthAccessGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("app_user");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).ValueGeneratedNever();
            b.Property(u => u.UserName).HasColumnType("citext");
            b.Property(u => u.NormalizedUserName).HasColumnType("citext");
            b.Property(u => u.Email).HasColumnType("citext");
            b.Property(u => u.NormalizedEmail).HasColumnType("citext");
            b.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");

            // Matches ux_app_user_name / ux_app_user_email: unique per tenant,
            // because accounts are per-tenant (docs/plan/08-decisions.md D9).
            b.HasIndex(u => new { u.TenantId, u.NormalizedUserName }).IsUnique();
            b.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();
            b.HasIndex(u => new { u.TenantId, u.PhoneBlindIndex });
        });

        modelBuilder.Entity<GuardianLink>(b =>
        {
            b.ToTable("guardian_link");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.HasIndex(l => new { l.GuardianUserId, l.ChildUserId }).IsUnique();
            b.HasIndex(l => l.ChildUserId);
        });

        modelBuilder.Entity<ParentalConsent>(b =>
        {
            b.ToTable("parental_consent");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            // ux_parental_consent_active is partial on revoked_at IS NULL; EF
            // expresses that as a filtered index so the model matches the DDL.
            b.HasIndex(c => new { c.ChildUserId, c.Scope })
             .IsUnique()
             .HasFilter("revoked_at is null");
        });

        modelBuilder.Entity<YouthCapabilityRecord>(b =>
        {
            b.ToTable("youth_capability");
            b.HasKey(c => c.UserId);
            b.Property(c => c.UserId).ValueGeneratedNever().HasColumnName("user_id");
            b.Property(c => c.CanBeMessagedByOrg).HasColumnName("can_be_messaged_by_org");
        });

        modelBuilder.Entity<RestrictedContact>(b =>
        {
            b.ToTable("restricted_contact");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.HasIndex(r => r.ProtectedUserId);
            b.HasIndex(r => r.RestrictedUserId);
        });

        modelBuilder.Entity<YouthAccessGrant>(b =>
        {
            b.ToTable("youth_access_grant");
            b.HasKey(g => g.Id);
            b.Property(g => g.Id).ValueGeneratedNever();
            b.Property(g => g.Fields).HasColumnType("text[]");
            // ix_youth_access_lookup: the shape every authorisation check uses.
            b.HasIndex(g => new { g.TenantId, g.OrgId, g.YouthUserId, g.EndsAt });
            b.HasIndex(g => g.YouthUserId);
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
