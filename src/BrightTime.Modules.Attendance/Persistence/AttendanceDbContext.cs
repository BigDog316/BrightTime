using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Attendance.Persistence;

// The attendance tables (db/schema/55-attendance.sql). note_enc holds AES-256-GCM ciphertext
// under the tenant's data key (D32); only this module encrypts and decrypts it.

/// <summary>One young person at one meeting. Corrections supersede; nothing is edited in place.</summary>
public sealed class AttendanceRecordRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public Guid ListingId { get; init; }

    public Guid EnrollmentId { get; init; }

    public DateTimeOffset OccurrenceStartsAt { get; init; }

    public required string State { get; init; }

    public short? MinutesAttended { get; init; }

    public DateTimeOffset? CheckedInAt { get; init; }

    public DateTimeOffset? CheckedOutAt { get; init; }

    public required string Method { get; init; }

    public Guid? RecordedBy { get; init; }

    public DateTimeOffset RecordedAt { get; init; }

    public string? DeviceId { get; init; }

    public Guid? ClientRecordId { get; init; }

    public DateTimeOffset? SyncedAt { get; init; }

    public byte[]? NoteEnc { get; init; }

    public Guid? SupersededBy { get; set; }
}

/// <summary>One offline batch from one device, with what became of it.</summary>
public sealed class AttendanceSyncBatchRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string DeviceId { get; init; }

    public Guid? SubmittedBy { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? CapturedFrom { get; init; }

    public DateTimeOffset? CapturedTo { get; init; }

    public int ItemCount { get; set; }

    public int AppliedCount { get; set; }

    public int ConflictCount { get; set; }

    public string? AppVersion { get; init; }
}

/// <summary>
/// A short-lived check-in code (D50). Only its SHA-256 is kept; the code itself is random and
/// never contains a youth identifier.
/// </summary>
public sealed class CheckinTokenRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? EnrollmentId { get; init; }

    public Guid? ListingId { get; init; }

    public required byte[] TokenHash { get; init; }

    public required string TokenKind { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public Guid? ConsumedBy { get; set; }
}

/// <summary>
/// A phone or kiosk an organisation lets hold an offline roster and sync attendance (D51).
/// Revoking it asks it to wipe itself on next contact.
/// </summary>
public sealed class KioskDeviceRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public Guid? SiteId { get; set; }

    public required string DeviceId { get; init; }

    public required string Label { get; set; }

    public Guid? EnrolledBy { get; init; }

    public DateTimeOffset EnrolledAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string? AppVersion { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public DateTimeOffset? WipeRequestedAt { get; set; }
}

/// <summary>The Attendance module's context (M3b): records, sync batches, check-in codes and devices.</summary>
public sealed class AttendanceDbContext(DbContextOptions<AttendanceDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "attendance";
    public const string MigrationsHistoryTable = "__ef_migrations_attendance";

    public override string Schema => SchemaName;

    public DbSet<AttendanceRecordRecord> Records => Set<AttendanceRecordRecord>();

    public DbSet<AttendanceSyncBatchRecord> SyncBatches => Set<AttendanceSyncBatchRecord>();

    public DbSet<CheckinTokenRecord> CheckinTokens => Set<CheckinTokenRecord>();

    public DbSet<KioskDeviceRecord> Devices => Set<KioskDeviceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AttendanceRecordRecord>(b =>
        {
            b.ToTable("attendance_record");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.EnrollmentId, x.OccurrenceStartsAt }).IsUnique().HasFilter("superseded_by is null");
            b.HasIndex(x => new { x.DeviceId, x.ClientRecordId }).IsUnique().HasFilter("client_record_id is not null");
            b.HasIndex(x => new { x.ListingId, x.OccurrenceStartsAt }).HasFilter("superseded_by is null");
            b.HasIndex(x => new { x.OrgId, x.OccurrenceStartsAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<AttendanceSyncBatchRecord>(b =>
        {
            b.ToTable("attendance_sync_batch");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.DeviceId, x.ReceivedAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<CheckinTokenRecord>(b =>
        {
            b.ToTable("checkin_token");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.ExpiresAt).HasFilter("consumed_at is null");
        });

        modelBuilder.Entity<KioskDeviceRecord>(b =>
        {
            b.ToTable("kiosk_device");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.TenantId, x.DeviceId }).IsUnique();
            b.HasIndex(x => x.OrgId).HasFilter("revoked_at is null");
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
