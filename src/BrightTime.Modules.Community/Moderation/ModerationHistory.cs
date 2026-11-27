using System.Text.Json;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>What happened to a message, one thing at a time (D116).</summary>
public static class HistoryKinds
{
    /// <summary>Screening looked at it and let it through (<see cref="HistoryEvent.Codes"/>: none).</summary>
    public const string Passed = "screened_passed";

    /// <summary>Screening held it (<see cref="HistoryEvent.Codes"/>: the screen's reasons).</summary>
    public const string Held = "screened_held";

    /// <summary>Someone reported it (<see cref="HistoryEvent.Codes"/>: reason, severity; the actor is a role).</summary>
    public const string Reported = "reported";

    /// <summary>A moderator took a report on.</summary>
    public const string TakenOn = "taken_on";

    /// <summary>A moderator's decision (<see cref="HistoryEvent.Codes"/>: the <c>moderation_action</c>).</summary>
    public const string Decided = "decided";

    /// <summary>An escalation's outcome, recorded.</summary>
    public const string EscalationClosed = "escalation_closed";
}

/// <summary>
/// One thing that happened to a message. <see cref="Actor"/> is a moderator's address, or the
/// role of whoever reported it; never a family's or young person's name.
/// </summary>
public sealed record HistoryEvent(DateTimeOffset? At, string Kind, string? Actor, string? Note, IReadOnlyList<string> Codes, Guid? ReportId);

/// <summary>
/// A message's moderation story (D116): the message as it stands, and everything screening,
/// reporters and moderators did to it, in order, rebuilt from <c>screening_result</c>,
/// <c>report</c> and the append-only <c>moderation_action</c>.
/// </summary>
[YouthData]
public sealed record MessageHistory(
    Guid MessageId,
    string OrgName,
    string? Subject,
    string SenderRole,
    string Body,
    string State,
    DateTimeOffset SentAt,
    IReadOnlyList<HistoryEvent> Events);

/// <summary>A moderator's decision, for the list of recent ones (D116).</summary>
public sealed record RecentDecision(DateTimeOffset At, string Action, string Moderator, string Reason, Guid MessageId, string OrgName, Guid? ReportId);

/// <summary>
/// The moderation trail, read back (D116): one message's whole story, and the county's recent
/// decisions. For the queue's moderators, never of their own organisation's conversations; every
/// read is audited.
/// </summary>
/// <remarks>
/// <para>
/// Built from the records each step wrote, not from the audit log: screening's own result (when
/// and why it held), each report (who reported it, by role, why, and when it was taken on), and
/// each <c>moderation_action</c> (who decided what, when, and the note), which can't be changed.
/// A message screened before D116 has no screening record; its hold shows without a time, from
/// the message's own <c>screen_result</c>.
/// </para>
/// <para>
/// "Taken on" is the report's acknowledgement, by whoever it was assigned to; it isn't shown
/// apart when a decision by the same moderator acknowledged it at the same moment.
/// </para>
/// </remarks>
public sealed class ModerationHistory(
    CommunityDbContext db,
    AuditTrail audit,
    IIdentityQueries identity,
    IDirectoryQueries directory)
{
    public const string Read = "moderation.history_read";

    public const int RecentCount = 50;

    private const string MessageTarget = "message";

    /// <summary>One message's story, for a moderator who isn't of its organisation.</summary>
    public Task<Result<MessageHistory>> ForMessageAsync(UserId moderator, Guid messageId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct).ConfigureAwait(false) is not { } message)
            {
                return Result<MessageHistory>.Failure(Error.NotFound("moderation.no_message", "There's no message like that."));
            }

            ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
            OrganisationId org = new(conversation.OrgId);
            if (message.SenderUserId == moderator.Value
                || (await directory.GetOrganisationMembersAsync(org, ct).ConfigureAwait(false)).Any(m => m.UserId == moderator))
            {
                return Result<MessageHistory>.Failure(Error.Forbidden("moderation.own_organisation",
                    "You're a member of the organisation that wrote this message, so another moderator decides it."));
            }

            Dictionary<Guid, string> roles = await db.Participants.AsNoTracking()
                .Where(p => p.ConversationId == conversation.Id)
                .ToDictionaryAsync(p => p.UserId, p => p.ParticipantRole, ct).ConfigureAwait(false);
            List<ScreeningResultRecord> screened = await db.ScreeningResults.AsNoTracking()
                .Where(s => s.TargetKind == MessageTarget && s.TargetId == messageId).ToListAsync(ct).ConfigureAwait(false);
            List<ReportRecord> reports = await db.Reports.AsNoTracking()
                .Where(r => r.TargetKind == MessageTarget && r.TargetId == messageId).ToListAsync(ct).ConfigureAwait(false);
            List<ModerationActionRecord> actions = await db.ModerationActions.AsNoTracking()
                .Where(a => a.TargetKind == MessageTarget && a.TargetId == messageId).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyDictionary<Guid, string> names = await identity.GetAdultEmailsAsync(
                [.. actions.Select(a => a.ActorUserId), .. reports.Where(r => r.AssignedTo is not null).Select(r => r.AssignedTo!.Value)], ct).ConfigureAwait(false);
            string Name(Guid? user) => user is { } u && names.TryGetValue(u, out string? n) ? n : "";

            List<HistoryEvent> events = [];
            foreach (ScreeningResultRecord s in screened)
            {
                events.Add(new HistoryEvent(s.CreatedAt, s.Decision == "pass" ? HistoryKinds.Passed : HistoryKinds.Held, null, null, Labels(s.Labels), null));
            }

            if (screened.Count == 0 && HeldMessages.Reasons(message.ScreenResult) is { Count: > 0 } before)
            {
                events.Add(new HistoryEvent(null, HistoryKinds.Held, null, null, before, null));
            }

            foreach (ReportRecord r in reports)
            {
                events.Add(new HistoryEvent(r.CreatedAt, HistoryKinds.Reported, r.ReporterUserId is { } who ? roles.GetValueOrDefault(who, "") : "", r.Details, [r.ReasonCode, r.Severity], r.Id));
                bool decidedThen = actions.Any(a => a.ReportId == r.Id && a.OccurredAt == r.AcknowledgedAt && a.ActorUserId == r.AssignedTo);
                if (r.AcknowledgedAt is { } taken && !decidedThen)
                {
                    events.Add(new HistoryEvent(taken, HistoryKinds.TakenOn, Name(r.AssignedTo), null, [], r.Id));
                }
            }

            foreach (ModerationActionRecord a in actions)
            {
                // Closing an escalation is a no_action on an escalated report, after its escalation.
                bool closes = a.Action == "no_action" && a.ReportId is { } rid
                    && actions.Any(e => e.ReportId == rid && e.Action == "escalate_external" && e.OccurredAt < a.OccurredAt);
                events.Add(new HistoryEvent(a.OccurredAt, closes ? HistoryKinds.EscalationClosed : HistoryKinds.Decided, Name(a.ActorUserId), a.Reason, [a.Action], a.ReportId));
            }

            List<HistoryEvent> ordered = [.. events.OrderBy(e => e.At ?? DateTimeOffset.MinValue).ThenBy(e => Order(e.Kind))];
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = MessageTarget, SubjectIds = [messageId] }, ct).ConfigureAwait(false);
            return Result<MessageHistory>.Success(new MessageHistory(
                message.Id,
                await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "",
                conversation.Subject,
                roles.GetValueOrDefault(message.SenderUserId, ""),
                message.Body,
                message.State,
                message.SentAt,
                ordered));
        }, cancellationToken);

    /// <summary>The county's most recent decisions on messages, newest first, leaving out the moderator's own organisations'.</summary>
    public Task<IReadOnlyList<RecentDecision>> RecentAsync(UserId moderator, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<RecentDecision>>(async ct =>
        {
            var rows = await (from a in db.ModerationActions.AsNoTracking()
                              join m in db.Messages.AsNoTracking() on a.TargetId equals m.Id
                              join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                              where a.TargetKind == MessageTarget
                              orderby a.OccurredAt descending, a.Id descending
                              select new { a, c.OrgId, m.SenderUserId })
                .Take(RecentCount * 2).ToListAsync(ct).ConfigureAwait(false);
            Dictionary<Guid, (string Name, bool Mine)> orgs = [];
            List<(ModerationActionRecord Action, string Org)> kept = [];
            foreach (var row in rows)
            {
                if (!orgs.TryGetValue(row.OrgId, out (string Name, bool Mine) org))
                {
                    OrganisationId id = new(row.OrgId);
                    org = (await directory.GetOrganisationNameAsync(id, ct).ConfigureAwait(false) ?? "",
                        (await directory.GetOrganisationMembersAsync(id, ct).ConfigureAwait(false)).Any(m => m.UserId == moderator));
                    orgs[row.OrgId] = org;
                }

                if (!org.Mine && row.SenderUserId != moderator.Value && kept.Count < RecentCount)
                {
                    kept.Add((row.a, org.Name));
                }
            }

            IReadOnlyDictionary<Guid, string> names = await identity.GetAdultEmailsAsync([.. kept.Select(k => k.Action.ActorUserId)], ct).ConfigureAwait(false);
            List<RecentDecision> list = [.. kept.Select(k => new RecentDecision(k.Action.OccurredAt, k.Action.Action,
                names.GetValueOrDefault(k.Action.ActorUserId, ""), k.Action.Reason, k.Action.TargetId, k.Org, k.Action.ReportId))];
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = MessageTarget, SubjectIds = [.. list.Select(d => d.MessageId).Distinct()] }, ct).ConfigureAwait(false);
            return list;
        }, cancellationToken);

    /// <summary>At the same moment, the order things happen in.</summary>
    private static int Order(string kind) => kind switch
    {
        HistoryKinds.Held or HistoryKinds.Passed => 0,
        HistoryKinds.Reported => 1,
        HistoryKinds.TakenOn => 2,
        HistoryKinds.Decided => 3,
        _ => 4,
    };

    private static IReadOnlyList<string> Labels(string labels)
    {
        using JsonDocument document = JsonDocument.Parse(labels);
        return document.RootElement.ValueKind == JsonValueKind.Object ? [.. document.RootElement.EnumerateObject().Select(p => p.Name)] : [];
    }
}
