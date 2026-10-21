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

    public DateTimeOffset SentAt { get; init; }

    public DateTimeOffset? EditedAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    public Guid? RemovedBy { get; set; }
}

/// <summary>The Community module's context (M3b): messaging so far.</summary>
public sealed class CommunityDbContext(DbContextOptions<CommunityDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "community";
    public const string MigrationsHistoryTable = "__ef_migrations_community";

    public override string Schema => SchemaName;

    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();

    public DbSet<ConversationParticipantRecord> Participants => Set<ConversationParticipantRecord>();

    public DbSet<MessageRecord> Messages => Set<MessageRecord>();

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

        modelBuilder.ApplySnakeCaseNames();
    }
}
