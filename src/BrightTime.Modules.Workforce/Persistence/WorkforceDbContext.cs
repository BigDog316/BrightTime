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

/// <summary>The Workforce module's context (M6): mentors first (D129).</summary>
public sealed class WorkforceDbContext(DbContextOptions<WorkforceDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "workforce";
    public const string MigrationsHistoryTable = "__ef_migrations_workforce";

    public override string Schema => SchemaName;

    public DbSet<MentorProfileRecord> MentorProfiles => Set<MentorProfileRecord>();

    public DbSet<MentorAvailabilityRecord> MentorAvailability => Set<MentorAvailabilityRecord>();

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

        modelBuilder.ApplySnakeCaseNames();
    }
}
