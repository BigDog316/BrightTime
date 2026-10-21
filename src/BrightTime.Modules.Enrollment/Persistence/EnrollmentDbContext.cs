using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Persistence;

// The family profile's tables (db/schema/50-enrollment.sql). The *_enc columns hold AES-256-GCM
// ciphertext under the tenant's data key (D32); this module encrypts and decrypts them, and
// nothing else reads them.

public sealed class FamilyProfileRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OwnerUserId { get; init; }

    public short? HouseholdSize { get; set; }

    public string? PrimaryLanguage { get; set; }

    public bool NeedsInterpreter { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class FamilyMemberRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyProfileId { get; init; }

    public Guid? UserId { get; set; }

    public required string MemberRole { get; set; }

    public required byte[] FirstNameEnc { get; set; }

    public required byte[] LastNameEnc { get; set; }

    public string? PreferredName { get; set; }

    public byte[]? DateOfBirthEnc { get; set; }

    public DateOnly? BirthMonth { get; set; }

    public short? Grade { get; set; }

    public Guid? SchoolId { get; set; }

    public Guid? NeighbourhoodId { get; set; }

    public string? Pronouns { get; set; }

    public string? RelationshipToOwner { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SupportNeedRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyMemberId { get; init; }

    public required string NeedKind { get; init; }

    public byte[]? DetailEnc { get; init; }

    public bool ShareWithProviders { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class FamilyContactRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyProfileId { get; init; }

    public required byte[] NameEnc { get; set; }

    public required string Relationship { get; set; }

    public required byte[] PhoneEnc { get; set; }

    public byte[]? PhoneBlindIndex { get; set; }

    public byte[]? EmailEnc { get; set; }

    public bool IsEmergency { get; set; }

    public bool CanPickUp { get; set; }

    public short SortOrder { get; set; }
}

/// <summary>One organisation's view of a family profile: which members, which fields, until when (D44).</summary>
public sealed class ProfileShareRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyProfileId { get; init; }

    public Guid OrgId { get; init; }

    public Guid? ListingId { get; init; }

    public required Guid[] MemberIds { get; init; }

    public required string[] SharedFields { get; init; }

    public Guid GrantedByUserId { get; init; }

    public DateTimeOffset GrantedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>The Enrollment module's context (M3a): the family profile and its shares so far.</summary>
public sealed class EnrollmentDbContext(DbContextOptions<EnrollmentDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "enrollment";
    public const string MigrationsHistoryTable = "__ef_migrations_enrollment";

    public override string Schema => SchemaName;

    public DbSet<FamilyProfileRecord> FamilyProfiles => Set<FamilyProfileRecord>();

    public DbSet<FamilyMemberRecord> FamilyMembers => Set<FamilyMemberRecord>();

    public DbSet<SupportNeedRecord> SupportNeeds => Set<SupportNeedRecord>();

    public DbSet<FamilyContactRecord> FamilyContacts => Set<FamilyContactRecord>();

    public DbSet<ProfileShareRecord> ProfileShares => Set<ProfileShareRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FamilyProfileRecord>(b =>
        {
            b.ToTable("family_profile");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.OwnerUserId).IsUnique();
        });

        modelBuilder.Entity<FamilyMemberRecord>(b =>
        {
            b.ToTable("family_member");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyProfileId);
            b.HasIndex(x => x.UserId).HasFilter("user_id is not null");
        });

        modelBuilder.Entity<SupportNeedRecord>(b =>
        {
            b.ToTable("family_member_support_need");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyMemberId);
        });

        modelBuilder.Entity<FamilyContactRecord>(b =>
        {
            b.ToTable("family_contact");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyProfileId);
        });

        modelBuilder.Entity<ProfileShareRecord>(b =>
        {
            b.ToTable("profile_share");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.OrgId, x.GrantedAt }).IsDescending(false, true).HasFilter("revoked_at is null");
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
