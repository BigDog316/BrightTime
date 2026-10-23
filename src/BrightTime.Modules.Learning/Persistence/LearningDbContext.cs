using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Persistence;

// The learning tables (db/schema/60-learning.sql) this module uses so far (D65, D66). Mapped as the
// DDL has them; the schema drift test holds the two together.

/// <summary>A young person's portfolio: how they appear, and who may see it.</summary>
public sealed class LearnerProfileRecord
{
    public Guid UserId { get; init; }

    public Guid TenantId { get; init; }

    public required string DisplayName { get; set; }

    public string? Headline { get; set; }

    public string? Bio { get; set; }

    public Guid? AvatarMediaId { get; set; }

    public required string Visibility { get; set; }

    public string? PublicSlug { get; set; }

    public decimal TotalHours { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One young person's part in one programme. Its title and organisation are copied, so the
/// portfolio outlives the listing or the organisation.
/// </summary>
public sealed class ParticipationRecordRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; set; }

    public Guid? ListingId { get; set; }

    public Guid? EnrollmentId { get; init; }

    public Guid? OrgId { get; set; }

    public required string TitleSnapshot { get; set; }

    public string? OrgNameSnapshot { get; set; }

    public DateOnly StartedOn { get; set; }

    public DateOnly? EndedOn { get; set; }

    public int SessionsAttended { get; set; }

    public decimal Hours { get; set; }

    public required string Source { get; init; }

    public Guid? VerifiedBy { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public bool IsHidden { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Who issues badges (D66): an organisation, under its own <c>did:web</c> DID. The DID names the
/// organisation by id, so it never changes when the organisation is renamed.
/// </summary>
public sealed class IssuerProfileRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? OrgId { get; init; }

    public required string Did { get; init; }

    public required string Name { get; set; }

    public string? Url { get; set; }

    public Guid? ImageMediaId { get; set; }

    public string? Email { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// One of an issuer's signing keys: its public half, and a reference to the private half in the
/// vault. The private key is never here.
/// </summary>
public sealed class IssuerKeyRecord
{
    public Guid Id { get; init; }

    public Guid IssuerId { get; init; }

    public required string KeyId { get; init; }

    public required string Algorithm { get; init; }

    public required string PublicJwk { get; init; }

    public required string PrivateKeyRef { get; init; }

    public required string Status { get; set; }

    public DateTimeOffset ActivatedAt { get; init; }

    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>An achievement an organisation awards (the OB 2.0 BadgeClass, the OB 3.0 Achievement).</summary>
public sealed class BadgeClassRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid IssuerId { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public required string CriteriaMd { get; set; }

    public string? CriteriaUrl { get; set; }

    public Guid? ImageMediaId { get; set; }

    public int Version { get; init; }

    public string? Level { get; set; }

    public string[] Tags { get; set; } = [];

    public string Alignment { get; set; } = "[]";

    public short? ExpiresAfterMonths { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// A badge awarded to one young person: the signed OB 3.0 credential, and what the OB 2.0 hosted
/// assertion and the public verification page are built from. The recipient appears only as a
/// salted hash.
/// </summary>
public sealed class BadgeAssertionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid BadgeClassId { get; init; }

    public Guid RecipientUserId { get; init; }

    public required byte[] RecipientIdentityHash { get; init; }

    public required byte[] RecipientSalt { get; init; }

    public DateTimeOffset IssuedOn { get; init; }

    public DateTimeOffset? ExpiresOn { get; init; }

    public string? Narrative { get; init; }

    public string Evidence { get; init; } = "[]";

    public Guid? IssuerKeyId { get; init; }

    public string? CredentialJwt { get; init; }

    public string? CredentialJson { get; init; }

    public required string VerifySlug { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevocationReason { get; set; }

    public Guid? AwardedBy { get; init; }

    public Guid? EnrollmentId { get; init; }

    public Guid? SourcePlaylistEnrollmentId { get; init; }
}

public sealed class LearningDbContext(DbContextOptions<LearningDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "learning";
    public const string MigrationsHistoryTable = "__ef_migrations_learning";

    public override string Schema => SchemaName;

    public DbSet<LearnerProfileRecord> Profiles => Set<LearnerProfileRecord>();

    public DbSet<ParticipationRecordRecord> Participation => Set<ParticipationRecordRecord>();

    public DbSet<IssuerProfileRecord> Issuers => Set<IssuerProfileRecord>();

    public DbSet<IssuerKeyRecord> IssuerKeys => Set<IssuerKeyRecord>();

    public DbSet<BadgeClassRecord> BadgeClasses => Set<BadgeClassRecord>();

    public DbSet<BadgeAssertionRecord> BadgeAssertions => Set<BadgeAssertionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LearnerProfileRecord>(b =>
        {
            b.ToTable("learner_profile");
            b.HasKey(x => x.UserId);
            b.Property(x => x.UserId).ValueGeneratedNever();
            b.Property(x => x.PublicSlug).HasColumnType("citext");
            b.Property(x => x.TotalHours).HasColumnType("numeric(8,2)");
            b.HasIndex(x => new { x.TenantId, x.PublicSlug }).IsUnique();
        });

        modelBuilder.Entity<ParticipationRecordRecord>(b =>
        {
            b.ToTable("participation_record");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Hours).HasColumnType("numeric(7,2)");
            b.HasIndex(x => new { x.UserId, x.StartedOn }).IsDescending(false, true).HasFilter("not is_hidden");
            b.HasIndex(x => x.EnrollmentId).IsUnique().HasFilter("enrollment_id is not null");
        });

        modelBuilder.Entity<IssuerProfileRecord>(b =>
        {
            b.ToTable("issuer_profile");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasColumnType("citext");
            b.HasIndex(x => x.Did).IsUnique();
        });

        modelBuilder.Entity<IssuerKeyRecord>(b =>
        {
            b.ToTable("issuer_key");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PublicJwk).HasColumnType("jsonb");
            b.HasIndex(x => new { x.IssuerId, x.KeyId }).IsUnique();
            b.HasIndex(x => x.IssuerId).IsUnique().HasFilter("status = 'active'");
        });

        modelBuilder.Entity<BadgeClassRecord>(b =>
        {
            b.ToTable("badge_class");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Slug).HasColumnType("citext");
            b.Property(x => x.Alignment).HasColumnType("jsonb");
            b.HasIndex(x => new { x.TenantId, x.Slug, x.Version }).IsUnique();
        });

        modelBuilder.Entity<BadgeAssertionRecord>(b =>
        {
            b.ToTable("badge_assertion");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Evidence).HasColumnType("jsonb");
            b.Property(x => x.CredentialJson).HasColumnType("jsonb");
            b.Property(x => x.VerifySlug).HasColumnType("citext");
            b.HasIndex(x => x.VerifySlug).IsUnique();
            b.HasIndex(x => new { x.BadgeClassId, x.RecipientUserId, x.IssuedOn }).IsUnique();
            b.HasIndex(x => new { x.RecipientUserId, x.IssuedOn }).IsDescending(false, true).HasFilter("revoked_at is null");
            b.HasIndex(x => x.BadgeClassId);
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
