using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Persistence;

// The messaging tables (db/schema/70-community.sql). includes_minor and adult_participant_count
// are the database's to keep: enforce_safe_conversation() recomputes them on every participant
// change and refuses a conversation with a minor and fewer than two adults. This module reads
// them and never writes them.

public sealed class ConversationRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public Guid? SpaceId { get; init; }

    public Guid? ListingId { get; init; }

    public required string ConversationKind { get; init; }

    public string? Subject { get; init; }

    public bool IsOrgVisible { get; init; } = true;

    public bool IncludesMinor { get; init; }

    public short AdultParticipantCount { get; init; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class ConversationParticipantRecord
{
    public Guid ConversationId { get; init; }

    public Guid UserId { get; init; }

    public required string ParticipantRole { get; init; }

    public bool IsMinor { get; init; }

    public bool IsImplicitReader { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public DateTimeOffset? RemovedAt { get; set; }

    public DateTimeOffset? LastReadAt { get; set; }
}

public sealed class MessageRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ConversationId { get; init; }

    public Guid SenderUserId { get; init; }

    public required string Body { get; init; }

    public required string State { get; set; }

    public string? ScreenResult { get; set; }

    /// <summary>When it was written; for a held message a moderator released, when it was released (D114).</summary>
    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset? EditedAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    public Guid? RemovedBy { get; set; }
}

/// <summary>
/// A moderator's decision (D114), in <c>moderation_action</c>: append-only, by a trigger and by the
/// application roles having no update or delete on it.
/// </summary>
public sealed class ModerationActionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? ReportId { get; init; }

    public required string TargetKind { get; init; }

    public Guid TargetId { get; init; }

    public required string Action { get; init; }

    public Guid ActorUserId { get; init; }

    public required string Reason { get; init; }

    public int? DurationHours { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// What screening decided about a piece of content, and when (<c>community.screening_result</c>):
/// so far, D53's contact-exchange screen on an adult's message to a young person (D116).
/// </summary>
public sealed class ScreeningResultRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string TargetKind { get; init; }

    public Guid TargetId { get; init; }

    public required string Provider { get; init; }

    public required string Decision { get; init; }

    public required string Labels { get; init; }

    public required string PiiSpans { get; init; }

    public string? ModelVersion { get; init; }

    public int? LatencyMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A moderator's mute or suspension of a person (D119, <c>community.person_restriction</c>).</summary>
public sealed class PersonRestrictionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    public required string Kind { get; init; }

    public required string Reason { get; init; }

    public Guid ImposedBy { get; init; }

    public Guid? SourceMessageId { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public DateTimeOffset? LiftedAt { get; set; }

    public Guid? LiftedBy { get; set; }

    public string? LiftReason { get; set; }
}

/// <summary>A report of something someone saw (D115, <c>community.report</c>): so far, a message.</summary>
public sealed class ReportRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string TargetKind { get; init; }

    public Guid TargetId { get; init; }

    public Guid? TargetSpaceId { get; init; }

    public Guid? ReporterUserId { get; init; }

    public bool ReporterIsMinor { get; init; }

    public required string ReasonCode { get; init; }

    public string? Details { get; init; }

    public required string Severity { get; init; }

    public required string State { get; set; }

    public Guid? AssignedTo { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? Resolution { get; set; }

    public DateTimeOffset? EscalatedAt { get; set; }

    public string? EscalationNote { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// The Community module's context (M3b): messaging, the moderation of what screening holds (D114),
/// and reports (D115).
/// </summary>
public sealed class CommunityDbContext(DbContextOptions<CommunityDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "community";
    public const string MigrationsHistoryTable = "__ef_migrations_community";

    public override string Schema => SchemaName;

    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();

    public DbSet<ConversationParticipantRecord> Participants => Set<ConversationParticipantRecord>();

    public DbSet<MessageRecord> Messages => Set<MessageRecord>();

    public DbSet<ModerationActionRecord> ModerationActions => Set<ModerationActionRecord>();

    public DbSet<ReportRecord> Reports => Set<ReportRecord>();

    public DbSet<ScreeningResultRecord> ScreeningResults => Set<ScreeningResultRecord>();

    public DbSet<PersonRestrictionRecord> PersonRestrictions => Set<PersonRestrictionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ConversationRecord>(b =>
        {
            b.ToTable("conversation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.OrgId, x.CreatedAt }).IsDescending(false, true);
            b.HasIndex(x => x.SpaceId).HasFilter("space_id is not null");
        });

        modelBuilder.Entity<ConversationParticipantRecord>(b =>
        {
            b.ToTable("conversation_participant");
            b.HasKey(x => new { x.ConversationId, x.UserId });
            b.HasIndex(x => x.UserId).HasFilter("removed_at is null");
        });

        modelBuilder.Entity<MessageRecord>(b =>
        {
            b.ToTable("message");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ScreenResult).HasColumnType("jsonb");
            b.HasIndex(x => new { x.ConversationId, x.SentAt }).IsDescending(false, true).HasFilter("state = 'sent'");
            b.HasIndex(x => new { x.TenantId, x.SentAt }).HasFilter("state in ('screening','held')");
        });

        modelBuilder.Entity<ModerationActionRecord>(b =>
        {
            b.ToTable("moderation_action");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.TargetKind, x.TargetId, x.OccurredAt }).IsDescending(false, false, true);
            b.HasIndex(x => new { x.ActorUserId, x.OccurredAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<PersonRestrictionRecord>(b =>
        {
            b.ToTable("person_restriction");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.UserId, x.Kind, x.EndsAt }).HasFilter("lifted_at is null");
            b.HasIndex(x => x.SourceMessageId).HasFilter("source_message_id is not null");
        });

        modelBuilder.Entity<ScreeningResultRecord>(b =>
        {
            b.ToTable("screening_result");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Labels).HasColumnType("jsonb");
            b.Property(x => x.PiiSpans).HasColumnType("jsonb");
            b.HasIndex(x => new { x.TargetKind, x.TargetId });
        });

        modelBuilder.Entity<ReportRecord>(b =>
        {
            b.ToTable("report");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.TenantId, x.Severity, x.CreatedAt }).IsDescending(false, true, false).HasFilter("state in ('open','triaged')");
            b.HasIndex(x => new { x.TargetKind, x.TargetId });
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
