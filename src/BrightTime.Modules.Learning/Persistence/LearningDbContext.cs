using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Persistence;

// The learning tables (db/schema/60-learning.sql) this module uses so far (D65 to D67). Mapped as the
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

/// <summary>A sequence of steps an organisation sets, with a badge at the end (D67).</summary>
public sealed class PlaylistRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? OrgId { get; init; }

    public required string Slug { get; init; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public Guid? HeroMediaId { get; set; }

    public bool IsOrdered { get; set; }

    public short? AgeMin { get; set; }

    public short? AgeMax { get; set; }

    public decimal? EstimatedHours { get; set; }

    public Guid? CompletionBadgeClassId { get; set; }

    public short? RequiredStepCount { get; set; }

    public required string Status { get; set; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PlaylistStepRecord
{
    public Guid Id { get; init; }

    public Guid PlaylistId { get; init; }

    public short SortOrder { get; init; }

    public required string StepKind { get; init; }

    public required string Title { get; init; }

    public string? InstructionsMd { get; init; }

    public Guid? ListingId { get; init; }

    public string? ExternalUrl { get; init; }

    public short? ExpectedMinutes { get; init; }

    public bool IsRequired { get; init; }

    public bool RequiresReview { get; init; }
}

/// <summary>One young person on one playlist.</summary>
public sealed class PlaylistEnrollmentRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid PlaylistId { get; init; }

    public Guid UserId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? AwardedAssertionId { get; set; }
}

/// <summary>Where one young person is on one step.</summary>
public sealed class PlaylistStepProgressRecord
{
    public Guid PlaylistEnrollmentId { get; init; }

    public Guid StepId { get; init; }

    public required string State { get; set; }

    public Guid? EvidenceProjectId { get; set; }

    public string? Reflection { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewNote { get; set; }
}

/// <summary>A badge that opens something to those who hold it; D67 uses it for playlists.</summary>
public sealed class UnlockRuleRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid BadgeClassId { get; set; }

    public required string UnlocksKind { get; init; }

    public Guid? UnlocksListingId { get; init; }

    public Guid? UnlocksPlaylistId { get; init; }
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

    public DbSet<PlaylistRecord> Playlists => Set<PlaylistRecord>();

    public DbSet<PlaylistStepRecord> PlaylistSteps => Set<PlaylistStepRecord>();

    public DbSet<PlaylistEnrollmentRecord> PlaylistEnrollments => Set<PlaylistEnrollmentRecord>();

    public DbSet<PlaylistStepProgressRecord> PlaylistProgress => Set<PlaylistStepProgressRecord>();

    public DbSet<UnlockRuleRecord> UnlockRules => Set<UnlockRuleRecord>();

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

        modelBuilder.Entity<PlaylistRecord>(b =>
        {
            b.ToTable("playlist");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Slug).HasColumnType("citext");
            b.Property(x => x.EstimatedHours).HasColumnType("numeric(6,2)");
            b.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
        });

        modelBuilder.Entity<PlaylistStepRecord>(b =>
        {
            b.ToTable("playlist_step");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.PlaylistId, x.SortOrder }).IsUnique();
        });

        modelBuilder.Entity<PlaylistEnrollmentRecord>(b =>
        {
            b.ToTable("playlist_enrollment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.PlaylistId, x.UserId }).IsUnique();
            b.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<PlaylistStepProgressRecord>(b =>
        {
            b.ToTable("playlist_step_progress");
            b.HasKey(x => new { x.PlaylistEnrollmentId, x.StepId });
        });

        modelBuilder.Entity<UnlockRuleRecord>(b =>
        {
            b.ToTable("unlock_rule");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.BadgeClassId);
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
