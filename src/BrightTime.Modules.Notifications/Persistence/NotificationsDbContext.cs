using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Notifications.Persistence;

/// <summary><c>notify.notification</c>: one thing a person was told, shown in their inbox.</summary>
public sealed class NotificationRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    public required string NotificationKind { get; init; }

    /// <summary>A catalogue key the client renders in its language, as search results' "why" is (D30).</summary>
    public required string Title { get; init; }

    public string? Body { get; init; }

    public string? DeepLink { get; init; }

    /// <summary>JSON: the title's arguments and anything the client needs to act on it.</summary>
    public string Payload { get; init; } = "{}";

    public string Priority { get; init; } = "normal";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary><c>notify.preference</c>: whether one kind arrives on one channel, and its quiet hours.</summary>
public sealed class PreferenceRecord
{
    public Guid UserId { get; init; }

    public Guid TenantId { get; init; }

    public required string NotificationKind { get; init; }

    public required string Channel { get; init; }

    public bool IsEnabled { get; set; } = true;

    public TimeOnly? QuietFromLocal { get; set; }

    public TimeOnly? QuietToLocal { get; set; }
}

/// <summary>
/// <c>notify.push_device</c>: a phone that may be sent pushes. The token is stored encrypted, and
/// found by a keyed hash, never in the clear.
/// </summary>
public sealed class PushDeviceRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; set; }

    public required string PlatformKind { get; set; }

    public required byte[] TokenHash { get; init; }

    public required byte[] TokenEnc { get; set; }

    public string? AppVersion { get; set; }

    public string? OsVersion { get; set; }

    public string? Locale { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>The Notifications module's context: the inbox, preferences and push devices.</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "notify";
    public const string MigrationsHistoryTable = "__ef_migrations_notify";

    public override string Schema => SchemaName;

    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

    public DbSet<PreferenceRecord> Preferences => Set<PreferenceRecord>();

    public DbSet<PushDeviceRecord> PushDevices => Set<PushDeviceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NotificationRecord>(b =>
        {
            b.ToTable("notification");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PreferenceRecord>(b =>
        {
            b.ToTable("preference");
            b.HasKey(x => new { x.UserId, x.NotificationKind, x.Channel });
        });

        modelBuilder.Entity<PushDeviceRecord>(b =>
        {
            b.ToTable("push_device");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.TokenHash).IsUnique();
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
