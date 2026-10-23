using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Persistence;

// The learning tables (db/schema/60-learning.sql) this module uses so far (D65). Mapped as the
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

public sealed class LearningDbContext(DbContextOptions<LearningDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "learning";
    public const string MigrationsHistoryTable = "__ef_migrations_learning";

    public override string Schema => SchemaName;

    public DbSet<LearnerProfileRecord> Profiles => Set<LearnerProfileRecord>();

    public DbSet<ParticipationRecordRecord> Participation => Set<ParticipationRecordRecord>();

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

        modelBuilder.ApplySnakeCaseNames();
    }
}
