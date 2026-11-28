using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Workforce.Persistence;

// The workforce tables (db/schema/80-workforce.sql). M6 starts with mentors (D129): their
// profiles and the hours they're free. Training, curriculum and the gig board come later.

/// <summary>A mentor's profile (<c>workforce.mentor_profile</c>): one per person, county-wide.</summary>
public sealed class MentorProfileRecord
{
    public Guid UserId { get; init; }

    public Guid TenantId { get; init; }

    /// <summary>The name organisations' staff find them by (D129).</summary>
    public string? DisplayName { get; set; }

    public string? Headline { get; set; }

    public string? Bio { get; set; }

    /// <summary>The schema's, unused so far: a picture waits for the media library's private copies.</summary>
    public Guid? AvatarMediaId { get; set; }

    /// <summary>Codes of the county's <c>language</c> terms.</summary>
    public string[] Languages { get; set; } = [];

    /// <summary>The county's <c>skill</c> terms.</summary>
    public Guid[] SkillTermIds { get; set; } = [];

    public Guid? HomeNeighbourhoodId { get; set; }

    public int? MaxTravelM { get; set; }

    public bool WillingVirtual { get; set; } = true;

    public short? WeeklyHoursSought { get; set; }

    public required string Status { get; set; }

    public bool IsSearchable { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A time a mentor is usually free (<c>workforce.mentor_availability</c>), in the county's time.</summary>
public sealed class MentorAvailabilityRecord
{
    public Guid Id { get; init; }

    public Guid MentorUserId { get; init; }

    /// <summary>0 for Sunday to 6 for Saturday.</summary>
    public short DayOfWeek { get; init; }

    public TimeOnly StartsAtLocal { get; init; }

    public TimeOnly EndsAtLocal { get; init; }

    /// <summary>The schema's, unused so far: availability is the usual week.</summary>
    public DateOnly? EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }
}

/// <summary>A course in the county's training catalogue (<c>workforce.course</c>, D130).</summary>
public sealed class CourseRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    /// <summary>The schema's, unused so far: the county writes courses, not organisations.</summary>
    public Guid? OrgId { get; init; }

    public required string Slug { get; set; }

    public required string Title { get; set; }

    public string? Summary { get; set; }

    public string? ProviderName { get; set; }

    public required string Delivery { get; set; }

    public decimal? Hours { get; set; }

    /// <summary>How long a completion counts; null for good.</summary>
    public short? ValidityMonths { get; set; }

    /// <summary>The schema's, unused so far: a badge for finishing waits on Learning.</summary>
    public Guid? BadgeClassId { get; init; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>One unit of a course (<c>workforce.course_unit</c>), in order.</summary>
public sealed class CourseUnitRecord
{
    public Guid Id { get; init; }

    public Guid CourseId { get; init; }

    public short SortOrder { get; init; }

    public required string Title { get; init; }

    public string? BodyMd { get; init; }

    /// <summary>The schema's, unused so far: a unit's file.</summary>
    public Guid? MediaId { get; init; }

    public string? ExternalUrl { get; init; }

    public short? Minutes { get; init; }
}

/// <summary>Someone taking a course (<c>workforce.course_enrollment</c>): when they started, finished, and until when it counts.</summary>
public sealed class CourseEnrollmentRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid CourseId { get; init; }

    public Guid UserId { get; init; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The schema's, unused so far: courses have no quiz.</summary>
    public decimal? Score { get; init; }

    public Guid? CertificateMediaId { get; init; }

    public Guid? BadgeAssertionId { get; init; }

    public DateOnly? ExpiresOn { get; set; }
}

/// <summary>A unit someone has done (<c>workforce.course_unit_progress</c>).</summary>
public sealed class CourseUnitProgressRecord
{
    public Guid CourseEnrollmentId { get; init; }

    public Guid UnitId { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>A course the county requires of a role (<c>workforce.training_requirement</c>).</summary>
public sealed class TrainingRequirementRecord
{
    public Guid TenantId { get; init; }

    public required string AppliesTo { get; init; }

    public Guid CourseId { get; init; }

    public bool IsBlocking { get; set; } = true;

    public short GraceDays { get; set; }
}

/// <summary>The Workforce module's context (M6): mentors (D129) and training (D130).</summary>
public sealed class WorkforceDbContext(DbContextOptions<WorkforceDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "workforce";
    public const string MigrationsHistoryTable = "__ef_migrations_workforce";

    public override string Schema => SchemaName;

    public DbSet<MentorProfileRecord> MentorProfiles => Set<MentorProfileRecord>();

    public DbSet<MentorAvailabilityRecord> MentorAvailability => Set<MentorAvailabilityRecord>();

    public DbSet<CourseRecord> Courses => Set<CourseRecord>();

    public DbSet<CourseUnitRecord> CourseUnits => Set<CourseUnitRecord>();

    public DbSet<CourseEnrollmentRecord> CourseEnrollments => Set<CourseEnrollmentRecord>();

    public DbSet<CourseUnitProgressRecord> UnitProgress => Set<CourseUnitProgressRecord>();

    public DbSet<TrainingRequirementRecord> TrainingRequirements => Set<TrainingRequirementRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MentorProfileRecord>(b =>
        {
            b.ToTable("mentor_profile");
            b.HasKey(x => x.UserId);
            b.Property(x => x.UserId).ValueGeneratedNever();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MentorAvailabilityRecord>(b =>
        {
            b.ToTable("mentor_availability");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.MentorUserId, x.DayOfWeek });
        });

        modelBuilder.Entity<CourseRecord>(b =>
        {
            b.ToTable("course");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Slug).HasColumnType("citext");
            b.Property(x => x.Hours).HasPrecision(5, 2);
            b.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
        });

        modelBuilder.Entity<CourseUnitRecord>(b =>
        {
            b.ToTable("course_unit");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.CourseId, x.SortOrder }).IsUnique();
        });

        modelBuilder.Entity<CourseEnrollmentRecord>(b =>
        {
            b.ToTable("course_enrollment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Score).HasPrecision(5, 2);
            b.HasIndex(x => new { x.CourseId, x.UserId }).IsUnique();
        });

        modelBuilder.Entity<CourseUnitProgressRecord>(b =>
        {
            b.ToTable("course_unit_progress");
            b.HasKey(x => new { x.CourseEnrollmentId, x.UnitId });
        });

        modelBuilder.Entity<TrainingRequirementRecord>(b =>
        {
            b.ToTable("training_requirement");
            b.HasKey(x => new { x.TenantId, x.AppliesTo, x.CourseId });
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
