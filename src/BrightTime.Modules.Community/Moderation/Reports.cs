using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>What the reporter is told: the report was taken, and how urgent it is.</summary>
public sealed record ReportReceipt(Guid Id, string Reason, string Severity);

/// <summary>
/// A report in the county's queue (D115), as a moderator reads it: the reason, the reporter's
/// role and words, and the message with the few before it, every person by role, never by name.
/// <see cref="Overdue"/> is a critical report not acknowledged within the hour;
/// <see cref="OwnOrganisation"/> is set when the moderator may not decide it.
/// </summary>
[YouthData]
public sealed record QueuedReport(
    Guid Id,
    string Reason,
    string Severity,
    string State,
    string ReporterRole,
    string? Details,
    DateTimeOffset ReportedAt,
    DateTimeOffset? AcknowledgedAt,
    bool Overdue,
    bool OwnOrganisation,
    Guid MessageId,
    string OrgName,
    string? Subject,
    string SenderRole,
    string Body,
    string MessageState,
    DateTimeOffset SentAt,
    IReadOnlyList<EarlierMessage> Earlier);

/// <summary>
/// Reporting a message (D115): someone in a conversation reports a message another person wrote
/// in it, with a reason, and it joins the county's moderation queue. A report of grooming or
/// self-harm is critical whoever makes it, goes to the top, is to be acknowledged within the hour,
/// and books an email to every moderator who may decide it.
/// </summary>
/// <remarks>
/// <para>
/// A moderator acknowledges a report, and then dismisses it, removes the message (as D114 removes
/// a held one, settling every open report of it), or escalates it: the mandated-reporter hand-off,
/// recorded with a note, the message removed too if the moderator says so. Each needs a note, is
/// decided once, and is a <c>moderation_action</c> row and an audit event. Nobody decides a report
/// of their own organisation's conversation, their own report or their own message.
/// </para>
/// <para>
/// A person reports a message once: reporting it again while the first is waiting returns the
/// first. The reported message stays where it is until a moderator decides.
/// </para>
/// </remarks>
public sealed class Reports(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory)
{
    public const string Made = "reports.made";
    public const string Read = "reports.read";
    public const string Acknowledged = "reports.acknowledged";
    public const string Dismissed = "reports.dismissed";
    public const string Removed = "reports.message_removed";
    public const string Escalated = "reports.escalated";

    public const int MaxDetails = 2000;
    public const int MaxNote = 1000;
    public const int PageSize = 100;

    /// <summary>
    /// Who is told of a critical report: <c>Permissions.Moderation.Triage</c> in Identity, which
    /// this module can't reference.
    /// </summary>
    public const string ModeratorPermission = "moderation.triage";

    private const string MessageTarget = "message";

    /// <summary>A person in the conversation reports a message someone else wrote in it.</summary>
    public Task<Result<ReportReceipt>> ReportMessageAsync(UserId reporter, Guid messageId, string? reason, string? details, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (reason is null || !ReportReasons.ForMessages.Contains(reason))
            {
                return Result<ReportReceipt>.Failure(Error.Validation("reports.reason", "Choose what's wrong with the message."));
            }

            string? said = details?.Trim() is { Length: > 0 } d ? d : null;
            if (said is { Length: > MaxDetails })
            {
                return Result<ReportReceipt>.Failure(Error.Validation("reports.details", $"Say what happened in up to {MaxDetails} characters."));
            }

            // Only a message the reporter can see: sent, someone else's, in a conversation they're in.
            MessageRecord? message = await db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == messageId && m.State == MessageStates.Sent && m.SenderUserId != reporter.Value, ct).ConfigureAwait(false);
            if (message is null || !await db.Participants.AsNoTracking()
                    .AnyAsync(p => p.ConversationId == message.ConversationId && p.UserId == reporter.Value && p.RemovedAt == null, ct).ConfigureAwait(false))
            {
                return Result<ReportReceipt>.Failure(Error.NotFound("reports.not_found", "There's no message like that in your conversations."));
            }

            if (await db.Reports.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.TargetKind == MessageTarget && r.TargetId == messageId && r.ReporterUserId == reporter.Value
                        && (r.State == ReportStates.Open || r.State == ReportStates.Triaged), ct).ConfigureAwait(false) is { } waiting)
            {
                return Result<ReportReceipt>.Success(new ReportReceipt(waiting.Id, waiting.ReasonCode, waiting.Severity));
            }

            ReportRecord report = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                TargetKind = MessageTarget,
                TargetId = messageId,
                ReporterUserId = reporter.Value,
                ReporterIsMinor = await identity.IsMinorAsync(reporter, ct).ConfigureAwait(false),
                ReasonCode = reason,
                Details = said,
                Severity = ReportReasons.SeverityOf(reason),
                State = ReportStates.Open,
                CreatedAt = clock.StoredNow(),
            };
            db.Reports.Add(report);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (report.Severity == ReportSeverities.Critical)
            {
                ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
                IReadOnlyList<OrganisationMembership> members = await directory.GetOrganisationMembersAsync(new OrganisationId(conversation.OrgId), ct).ConfigureAwait(false);
                foreach (Guid moderator in await identity.GetUsersWithPermissionAsync(ModeratorPermission, ct).ConfigureAwait(false))
                {
                    if (moderator == reporter.Value || moderator == message.SenderUserId || members.Any(m => m.UserId.Value == moderator))
                    {
                        continue;
                    }

                    CriticalReportNotice notice = new(moderator, report.Id, reason);
                    await db.EnqueueJobAsync(new JobRequest
                    {
                        JobType = CriticalReportNotice.JobType,
                        TenantId = tenant.TenantId,
                        Payload = notice,
                        UniqueKey = notice.UniqueKey,
                    }, ct).ConfigureAwait(false);
                }
            }

            await AuditAsync(Made, MessageTarget, [messageId], ct).ConfigureAwait(false);
            return Result<ReportReceipt>.Success(new ReportReceipt(report.Id, report.ReasonCode, report.Severity));
        }, cancellationToken);

    /// <summary>The reports waiting, critical first, then oldest; reading them is audited.</summary>
    public Task<IReadOnlyList<QueuedReport>> ListAsync(UserId moderator, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<QueuedReport>>(async ct =>
        {
            // The rank, not the text: ix_report_queue's "severity desc" would put normal first.
            List<ReportRecord> waiting = [.. (await db.Reports.AsNoTracking()
                    .Where(r => r.State == ReportStates.Open || r.State == ReportStates.Triaged)
                    .OrderBy(r => r.Severity == ReportSeverities.Critical ? 0 : r.Severity == ReportSeverities.High ? 1 : 2)
                    .ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
                    .Take(PageSize).ToListAsync(ct).ConfigureAwait(false))];
            DateTimeOffset now = clock.StoredNow();
            Dictionary<Guid, (string Name, bool Mine)> orgs = [];
            List<QueuedReport> list = [];
            foreach (ReportRecord report in waiting)
            {
                MessageRecord? message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == report.TargetId, ct).ConfigureAwait(false);
                if (message is null)
                {
                    continue;
                }

                ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
                if (!orgs.TryGetValue(conversation.OrgId, out (string Name, bool Mine) org))
                {
                    OrganisationId id = new(conversation.OrgId);
                    org = (await directory.GetOrganisationNameAsync(id, ct).ConfigureAwait(false) ?? "",
                        (await directory.GetOrganisationMembersAsync(id, ct).ConfigureAwait(false)).Any(m => m.UserId == moderator));
                    orgs[conversation.OrgId] = org;
                }

                Dictionary<Guid, string> roles = await db.Participants.AsNoTracking()
                    .Where(p => p.ConversationId == conversation.Id)
                    .ToDictionaryAsync(p => p.UserId, p => p.ParticipantRole, ct).ConfigureAwait(false);
                List<MessageRecord> earlier = await db.Messages.AsNoTracking()
                    .Where(m => m.ConversationId == conversation.Id && m.State == MessageStates.Sent && m.SentAt < message.SentAt)
                    .OrderByDescending(m => m.SentAt).Take(HeldMessages.EarlierCount).ToListAsync(ct).ConfigureAwait(false);
                earlier.Reverse();

                bool mine = org.Mine || report.ReporterUserId == moderator.Value || message.SenderUserId == moderator.Value;
                list.Add(new QueuedReport(
                    report.Id,
                    report.ReasonCode,
                    report.Severity,
                    report.State,
                    report.ReporterUserId is { } who ? roles.GetValueOrDefault(who, "") : "",
                    report.Details,
                    report.CreatedAt,
                    report.AcknowledgedAt,
                    report.Severity == ReportSeverities.Critical && report.AcknowledgedAt is null && now - report.CreatedAt > ReportSeverities.CriticalAcknowledgement,
                    mine,
                    message.Id,
                    org.Name,
                    conversation.Subject,
                    roles.GetValueOrDefault(message.SenderUserId, ""),
                    message.Body,
                    message.State,
                    message.SentAt,
                    [.. earlier.Select(m => new EarlierMessage(roles.GetValueOrDefault(m.SenderUserId, ""), m.Body, m.SentAt))]));
            }

            await AuditAsync(Read, "report", [.. list.Select(r => r.Id)], ct).ConfigureAwait(false);
            return list;
        }, cancellationToken);

    /// <summary>A moderator takes a report on: it's acknowledged, and theirs.</summary>
    public Task<Result> AcknowledgeAsync(UserId moderator, Guid reportId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await DecidableAsync(moderator, reportId, ct).ConfigureAwait(false) is { Refused: { } refused })
            {
                return Result.Failure(refused);
            }

            DateTimeOffset now = clock.StoredNow();
            int changed = await db.Reports.Where(r => r.Id == reportId && r.State == ReportStates.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, ReportStates.Triaged)
                    .SetProperty(r => r.AcknowledgedAt, now).SetProperty(r => r.AssignedTo, moderator.Value), ct).ConfigureAwait(false);
            if (changed == 0)
            {
                return Result.Failure(Error.Conflict("reports.acknowledged", "Someone has already taken this report on."));
            }

            await AuditAsync(Acknowledged, "report", [reportId], ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Nothing to do: the report is closed with the moderator's note, and the message stays.</summary>
    public Task<Result> DismissAsync(UserId moderator, Guid reportId, string? note, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, reportId, note, Dismissed, removeMessage: false, cancellationToken);

    /// <summary>The message is removed, and every report of it waiting is settled with the note.</summary>
    public Task<Result> RemoveMessageAsync(UserId moderator, Guid reportId, string? note, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, reportId, note, Removed, removeMessage: true, cancellationToken);

    /// <summary>
    /// The mandated-reporter hand-off: the report is escalated with the moderator's note of what was
    /// done and who was told, and the message is removed too when <paramref name="removeMessage"/>.
    /// </summary>
    public Task<Result> EscalateAsync(UserId moderator, Guid reportId, string? note, bool removeMessage, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, reportId, note, Escalated, removeMessage, cancellationToken);

    /// <summary>
    /// A decision on a waiting report: <see cref="Dismissed"/>, <see cref="Removed"/> or
    /// <see cref="Escalated"/>, which is also the audit action; the message removed when asked.
    /// </summary>
    private Task<Result> DecideAsync(UserId moderator, Guid reportId, string? note, string decision, bool removeMessage, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (note?.Trim() is not { Length: > 0 and <= MaxNote } why)
            {
                return Result.Failure(Error.Validation("reports.note", $"Write a note of up to {MaxNote} characters; it's kept in the moderation record."));
            }

            (ReportRecord? report, Error? refused) = await DecidableAsync(moderator, reportId, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return Result.Failure(refused);
            }

            ReportRecord decided = report!;
            DateTimeOffset now = clock.StoredNow();
            IQueryable<ReportRecord> waiting = db.Reports.Where(r => r.State == ReportStates.Open || r.State == ReportStates.Triaged);
            int changed = decision switch
            {
                Escalated => await waiting.Where(r => r.Id == reportId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, ReportStates.Escalated)
                        .SetProperty(r => r.EscalatedAt, now).SetProperty(r => r.EscalationNote, why)
                        .SetProperty(r => r.AcknowledgedAt, r => r.AcknowledgedAt ?? now)
                        .SetProperty(r => r.AssignedTo, r => r.AssignedTo ?? moderator.Value), ct).ConfigureAwait(false),

                // The message goes, so every report of it waiting is settled with it.
                Removed => await waiting.Where(r => r.TargetKind == MessageTarget && r.TargetId == decided.TargetId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, ReportStates.Actioned)
                        .SetProperty(r => r.ResolvedAt, now).SetProperty(r => r.Resolution, why)
                        .SetProperty(r => r.AcknowledgedAt, r => r.AcknowledgedAt ?? now)
                        .SetProperty(r => r.AssignedTo, r => r.AssignedTo ?? moderator.Value), ct).ConfigureAwait(false),

                _ => await waiting.Where(r => r.Id == reportId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, ReportStates.Dismissed)
                        .SetProperty(r => r.ResolvedAt, now).SetProperty(r => r.Resolution, why)
                        .SetProperty(r => r.AcknowledgedAt, r => r.AcknowledgedAt ?? now)
                        .SetProperty(r => r.AssignedTo, r => r.AssignedTo ?? moderator.Value), ct).ConfigureAwait(false),
            };
            if (changed == 0)
            {
                return Result.Failure(NotWaiting());
            }

            Act(decided, decision switch { Escalated => "escalate_external", Removed => "remove", _ => "no_action" }, moderator, why, now);
            if (removeMessage)
            {
                await db.Messages.Where(m => m.Id == decided.TargetId && m.State == MessageStates.Sent)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.State, MessageStates.Removed)
                        .SetProperty(m => m.RemovedAt, now).SetProperty(m => m.RemovedBy, moderator.Value), ct).ConfigureAwait(false);
                if (decision == Escalated)
                {
                    Act(decided, "remove", moderator, why, now);
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(decision, "report", [reportId], ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The report, if it's waiting and this moderator may decide it.</summary>
    private async Task<(ReportRecord? Report, Error? Refused)> DecidableAsync(UserId moderator, Guid reportId, CancellationToken ct)
    {
        ReportRecord? report = await db.Reports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && (r.State == ReportStates.Open || r.State == ReportStates.Triaged), ct).ConfigureAwait(false);
        MessageRecord? message = report is null ? null
            : await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == report.TargetId, ct).ConfigureAwait(false);
        if (report is null || message is null)
        {
            return (null, NotWaiting());
        }

        ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
        bool member = (await directory.GetOrganisationMembersAsync(new OrganisationId(conversation.OrgId), ct).ConfigureAwait(false)).Any(m => m.UserId == moderator);
        if (member || report.ReporterUserId == moderator.Value || message.SenderUserId == moderator.Value)
        {
            return (null, Error.Forbidden("reports.own_organisation",
                "You're in this conversation's organisation, or it's your own report or message, so another moderator decides it."));
        }

        return (report, null);
    }

    private void Act(ReportRecord report, string action, UserId moderator, string reason, DateTimeOffset at) =>
        db.ModerationActions.Add(new ModerationActionRecord
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            ReportId = report.Id,
            TargetKind = MessageTarget,
            TargetId = report.TargetId,
            Action = action,
            ActorUserId = moderator.Value,
            Reason = reason,
            OccurredAt = at,
        });

    private Task AuditAsync(string action, string kind, IReadOnlyCollection<Guid> subjects, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = kind,
            SubjectIds = subjects,
        }, ct);

    private static Error NotWaiting() => Error.NotFound("reports.not_waiting", "That report isn't waiting for a moderator any more.");
}
