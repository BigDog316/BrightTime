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

    /// <summary>The sender muted or suspended from this message (D119; <see cref="HistoryEvent.Codes"/>: mute or suspend; <see cref="HistoryEvent.Until"/>).</summary>
    public const string Restricted = "restricted";

    /// <summary>That mute or suspension lifted early.</summary>
    public const string RestrictionLifted = "restriction_lifted";
}

/// <summary>
/// One thing that happened to a message. <see cref="Actor"/> is a moderator's address, or the
/// role of whoever reported it; never a family's or young person's name.
/// </summary>
public sealed record HistoryEvent(DateTimeOffset? At, string Kind, string? Actor, string? Note, IReadOnlyList<string> Codes, Guid? ReportId, DateTimeOffset? Until = null);

/// <summary>
/// A message's or post's moderation story (D116, D121): it as it stands, and everything
/// screening, reporters and moderators did to it, in order, rebuilt from <c>screening_result</c>,
/// <c>report</c> and the append-only <c>moderation_action</c>. <see cref="Kind"/> is
/// <c>message</c> or <c>post</c>; <see cref="Where"/> the conversation's subject or the space's
/// name; roles are participant roles or space roles to match.
/// </summary>
[YouthData]
public sealed record ItemHistory(
    string Kind,
    Guid Id,
    string OrgName,
    string? Where,
    string AuthorRole,
    string Body,
    string State,
    DateTimeOffset WrittenAt,
    IReadOnlyList<HistoryEvent> Events,
    bool AuthorIsAdult = false);

/// <summary>A moderator's decision, for the list of recent ones (D116), on a message or a post.</summary>
public sealed record RecentDecision(DateTimeOffset At, string Action, string Moderator, string Reason, string TargetKind, Guid TargetId, string OrgName, Guid? ReportId);

/// <summary>
/// The moderation trail, read back (D116, D121): one message's or post's whole story, and the
/// county's recent decisions. For the queue's moderators, never of their own organisation's
/// conversations or spaces; every read is audited.
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
/// apart when a decision by the same moderator acknowledged it at the same moment. Muting and
/// suspending (D119) start from a message, so only a message's story has them.
/// </para>
/// </remarks>
public sealed class ModerationHistory(
    CommunityDbContext db,
    Infrastructure.Tenancy.ITenantContext tenant,
    AuditTrail audit,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    Learning.Contracts.IPortfolioModeration? portfolios = null)
{
    private readonly ReportTargets _targets = new(db, tenant, portfolios);

    public const string Read = "moderation.history_read";

    public const int RecentCount = 50;

    /// <summary>One message's story, for a moderator who isn't of its organisation.</summary>
    public Task<Result<ItemHistory>> ForMessageAsync(UserId moderator, Guid messageId, CancellationToken cancellationToken = default) =>
        ForAsync(moderator, ReportTargets.Message, messageId, cancellationToken);

    /// <summary>One post's story (D121), for a moderator who isn't of its space's organisation.</summary>
    public Task<Result<ItemHistory>> ForPostAsync(UserId moderator, Guid postId, CancellationToken cancellationToken = default) =>
        ForAsync(moderator, ReportTargets.Post, postId, cancellationToken);

    /// <summary>
    /// The story of anything moderation reads (D128): a showcase item, a piece of challenge work or
    /// a portfolio, as well as a message or a post.
    /// </summary>
    public Task<Result<ItemHistory>> ForItemAsync(UserId moderator, string? kind, Guid id, CancellationToken cancellationToken = default) =>
        kind is ReportTargets.Message or ReportTargets.Post or ReportTargets.ShowcaseItem or ReportTargets.Submission or ReportTargets.Portfolio
            ? ForAsync(moderator, kind, id, cancellationToken)
            : Task.FromResult(Result<ItemHistory>.Failure(Error.NotFound("moderation.no_item", "There's nothing like that.")));

    private Task<Result<ItemHistory>> ForAsync(UserId moderator, string kind, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (await _targets.FindAsync(kind, id, ct).ConfigureAwait(false) is not { } item)
            {
                return Result<ItemHistory>.Failure(kind switch
                {
                    ReportTargets.Post => Error.NotFound("moderation.no_post", "There's no post like that."),
                    ReportTargets.Message => Error.NotFound("moderation.no_message", "There's no message like that."),
                    _ => Error.NotFound("moderation.no_item", "There's nothing like that."),
                });
            }

            OrganisationId org = new(item.OrgId);
            if (item.AuthorUserId == moderator.Value
                || (item.OrgId != Guid.Empty && (await directory.GetOrganisationMembersAsync(org, ct).ConfigureAwait(false)).Any(m => m.UserId == moderator)))
            {
                return Result<ItemHistory>.Failure(Error.Forbidden("moderation.own_organisation",
                    "You're a member of the organisation that wrote this, so another moderator decides it."));
            }

            List<ScreeningResultRecord> screened = await db.ScreeningResults.AsNoTracking()
                .Where(s => s.TargetKind == kind && s.TargetId == id).OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
            List<ReportRecord> reports = await db.Reports.AsNoTracking()
                .Where(r => r.TargetKind == kind && r.TargetId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
            List<ModerationActionRecord> actions = await db.ModerationActions.AsNoTracking()
                .Where(a => a.TargetKind == kind && a.TargetId == id).OrderBy(a => a.OccurredAt).ToListAsync(ct).ConfigureAwait(false);
            List<PersonRestrictionRecord> restrictions = kind == ReportTargets.Message
                ? await db.PersonRestrictions.AsNoTracking().Where(r => r.SourceMessageId == id).OrderBy(r => r.StartsAt).ToListAsync(ct).ConfigureAwait(false)
                : [];
            IReadOnlyDictionary<Guid, string> names = await identity.GetAdultEmailsAsync(
                [.. actions.Select(a => a.ActorUserId), .. reports.Where(r => r.AssignedTo is not null).Select(r => r.AssignedTo!.Value),
                 .. restrictions.Select(r => r.ImposedBy), .. restrictions.Where(r => r.LiftedBy is not null).Select(r => r.LiftedBy!.Value)], ct).ConfigureAwait(false);
            string Name(Guid? user) => user is { } u && names.TryGetValue(u, out string? n) ? n : "";

            List<HistoryEvent> events = [];
            foreach (ScreeningResultRecord s in screened)
            {
                events.Add(new HistoryEvent(s.CreatedAt, s.Decision == "pass" ? HistoryKinds.Passed : HistoryKinds.Held, null, null, Labels(s.Labels), null));
            }

            if (screened.Count == 0 && HeldMessages.Reasons(item.ScreenResult) is { Count: > 0 } before)
            {
                events.Add(new HistoryEvent(null, HistoryKinds.Held, null, null, before, null));
            }

            foreach (ReportRecord r in reports)
            {
                events.Add(new HistoryEvent(r.CreatedAt, HistoryKinds.Reported, item.ReporterRole(r.ReporterUserId), r.Details, [r.ReasonCode, r.Severity], r.Id));
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

            foreach (PersonRestrictionRecord r in restrictions)
            {
                events.Add(new HistoryEvent(r.StartsAt, HistoryKinds.Restricted, Name(r.ImposedBy), r.Reason, [r.Kind], null, r.EndsAt));
                if (r.LiftedAt is { } lifted)
                {
                    events.Add(new HistoryEvent(lifted, HistoryKinds.RestrictionLifted, Name(r.LiftedBy), r.LiftReason, [r.Kind], null));
                }
            }

            // Two decisions in one moment (an escalation and the removal with it) by what they mean:
            // ids are no help, as UUIDv7 is random within a millisecond.
            List<HistoryEvent> ordered = [.. events.OrderBy(e => e.At ?? DateTimeOffset.MinValue).ThenBy(e => Order(e.Kind))
                .ThenBy(e => e.Kind == HistoryKinds.Decided ? ActionOrder(e.Codes[0]) : 0)];
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = kind, SubjectIds = [id] }, ct).ConfigureAwait(false);
            return Result<ItemHistory>.Success(new ItemHistory(
                kind,
                item.Id,
                item.OrgId == Guid.Empty ? "" : await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "",
                item.Where,
                item.AuthorRole,
                item.Body,
                item.State,
                item.At,
                ordered,
                !await identity.IsMinorAsync(new UserId(item.AuthorUserId), ct).ConfigureAwait(false)));
        }, cancellationToken);

    /// <summary>
    /// The county's most recent decisions on messages and posts, newest first, leaving out the
    /// moderator's own organisations' and what they wrote.
    /// </summary>
    public Task<IReadOnlyList<RecentDecision>> RecentAsync(UserId moderator, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<RecentDecision>>(async ct =>
        {
            List<ModerationActionRecord> actions = await db.ModerationActions.AsNoTracking()
                .Where(a => a.TargetKind == ReportTargets.Message || a.TargetKind == ReportTargets.Post || a.TargetKind == ReportTargets.ShowcaseItem
                            || a.TargetKind == ReportTargets.Submission || a.TargetKind == ReportTargets.Portfolio)
                .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
                .Take(RecentCount * 2).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> messageIds = [.. actions.Where(a => a.TargetKind == ReportTargets.Message).Select(a => a.TargetId).Distinct()];
            List<Guid> postIds = [.. actions.Where(a => a.TargetKind == ReportTargets.Post).Select(a => a.TargetId).Distinct()];
            Dictionary<(string Kind, Guid Id), (Guid Org, Guid Author)> whose = [];
            foreach (var m in await (from m in db.Messages.AsNoTracking()
                                     join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                                     where messageIds.Contains(m.Id)
                                     select new { m.Id, c.OrgId, m.SenderUserId }).ToListAsync(ct).ConfigureAwait(false))
            {
                whose[(ReportTargets.Message, m.Id)] = (m.OrgId, m.SenderUserId);
            }

            foreach (var p in await (from p in db.Posts.AsNoTracking()
                                     join s in db.Spaces.AsNoTracking() on p.SpaceId equals s.Id
                                     where postIds.Contains(p.Id)
                                     select new { p.Id, s.OrgId, p.AuthorUserId }).ToListAsync(ct).ConfigureAwait(false))
            {
                whose[(ReportTargets.Post, p.Id)] = (p.OrgId, p.AuthorUserId);
            }

            // The rest (D128), each read the one way.
            foreach (var other in actions.Where(a => a.TargetKind is not (ReportTargets.Message or ReportTargets.Post)).Select(a => (a.TargetKind, a.TargetId)).Distinct())
            {
                if (await _targets.FindAsync(other.TargetKind, other.TargetId, ct).ConfigureAwait(false) is { } found)
                {
                    whose[other] = (found.OrgId, found.AuthorUserId);
                }
            }

            Dictionary<Guid, (string Name, bool Mine)> orgs = [];
            List<(ModerationActionRecord Action, string Org)> kept = [];
            foreach (ModerationActionRecord a in actions)
            {
                if (kept.Count == RecentCount || !whose.TryGetValue((a.TargetKind, a.TargetId), out (Guid Org, Guid Author) of))
                {
                    continue;
                }

                if (of.Org == Guid.Empty)
                {
                    orgs[of.Org] = ("", false);
                }

                if (!orgs.TryGetValue(of.Org, out (string Name, bool Mine) org))
                {
                    OrganisationId id = new(of.Org);
                    org = (await directory.GetOrganisationNameAsync(id, ct).ConfigureAwait(false) ?? "",
                        (await directory.GetOrganisationMembersAsync(id, ct).ConfigureAwait(false)).Any(m => m.UserId == moderator));
                    orgs[of.Org] = org;
                }

                if (!org.Mine && of.Author != moderator.Value)
                {
                    kept.Add((a, org.Name));
                }
            }

            IReadOnlyDictionary<Guid, string> names = await identity.GetAdultEmailsAsync([.. kept.Select(k => k.Action.ActorUserId)], ct).ConfigureAwait(false);
            List<RecentDecision> list = [.. kept.Select(k => new RecentDecision(k.Action.OccurredAt, k.Action.Action,
                names.GetValueOrDefault(k.Action.ActorUserId, ""), k.Action.Reason, k.Action.TargetKind, k.Action.TargetId, k.Org, k.Action.ReportId))];
            // Always the messages' read, even of none: the page is a youth record (D116).
            List<Guid> messages = [.. list.Where(d => d.TargetKind == ReportTargets.Message).Select(d => d.TargetId).Distinct()];
            List<Guid> posts = [.. list.Where(d => d.TargetKind == ReportTargets.Post).Select(d => d.TargetId).Distinct()];
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = ReportTargets.Message, SubjectIds = messages }, ct).ConfigureAwait(false);
            if (posts.Count > 0)
            {
                await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = ReportTargets.Post, SubjectIds = posts }, ct).ConfigureAwait(false);
            }

            foreach (IGrouping<string, RecentDecision> others in list.Where(d => d.TargetKind is not (ReportTargets.Message or ReportTargets.Post)).GroupBy(d => d.TargetKind))
            {
                await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = others.Key, SubjectIds = [.. others.Select(d => d.TargetId).Distinct()] }, ct).ConfigureAwait(false);
            }

            return list;
        }, cancellationToken);

    /// <summary>At the same moment, the order things happen in.</summary>
    private static int Order(string kind) => kind switch
    {
        HistoryKinds.Held or HistoryKinds.Passed => 0,
        HistoryKinds.Reported => 1,
        HistoryKinds.TakenOn => 2,
        HistoryKinds.Decided => 3,
        HistoryKinds.Restricted => 4,
        _ => 5,
    };

    /// <summary>Within one moment: released, escalated, then removed, then anything else.</summary>
    private static int ActionOrder(string action) => action switch
    {
        "publish" => 0,
        "escalate_external" => 1,
        "remove" => 2,
        _ => 3,
    };

    private static IReadOnlyList<string> Labels(string labels)
    {
        using JsonDocument document = JsonDocument.Parse(labels);
        return document.RootElement.ValueKind == JsonValueKind.Object ? [.. document.RootElement.EnumerateObject().Select(p => p.Name)] : [];
    }
}
