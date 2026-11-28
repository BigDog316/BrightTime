using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>What the reporter is told: the report was taken, and how urgent it is.</summary>
public sealed record ReportReceipt(Guid Id, string Reason, string Severity);

/// <summary>
/// A report in the county's queue (D115, D121), as a moderator reads it: the reason, the
/// reporter's role and words, and what was reported (<see cref="TargetKind"/>: a <c>message</c> or
/// a <c>post</c>) with what came before it, every person by role, never by name.
/// <see cref="Where"/> is the conversation's subject or the space's name. <see cref="Overdue"/> is
/// a critical report not acknowledged within the hour; <see cref="OwnOrganisation"/> is set when
/// the moderator may not decide it.
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
    string TargetKind,
    Guid TargetId,
    string OrgName,
    string? Where,
    string AuthorRole,
    string Body,
    string TargetState,
    DateTimeOffset WrittenAt,
    IReadOnlyList<EarlierMessage> Earlier);

/// <summary>
/// An escalated report waiting for its outcome (D116): what was escalated, when, the moderator's
/// note of what they did and who they told, and what was reported, to open its history.
/// </summary>
[YouthData]
public sealed record EscalatedReport(
    Guid Id,
    string Reason,
    string Severity,
    DateTimeOffset EscalatedAt,
    string? EscalationNote,
    string TargetKind,
    Guid TargetId,
    string OrgName,
    bool OwnOrganisation);

/// <summary>
/// Reporting (D115, D121, D128): someone in a conversation reports a message another person wrote
/// in it; a member of a space a post another member wrote there; a space's staff a young person's
/// work for a challenge; and anyone, signed in or not, an item on the public showcase or a shown
/// portfolio. Each comes with a reason, and joins the county's moderation queue. A report of grooming or self-harm is critical whoever makes it, goes
/// to the top, is to be acknowledged within the hour, and books an email to every moderator who
/// may decide it.
/// </summary>
/// <remarks>
/// <para>
/// A moderator acknowledges a report, and then dismisses it, removes what was reported (settling
/// every open report of it), or escalates it: the mandated-reporter hand-off, recorded with a
/// note, what was reported removed too if the moderator says so. Each needs a note, is decided
/// once, and is a <c>moderation_action</c> row and an audit event. Nobody decides a report of
/// their own organisation's conversation or space, their own report, or what they wrote.
/// </para>
/// <para>
/// A person reports a thing once: reporting it again while the first is waiting returns the
/// first. What was reported stays where it is until a moderator decides. A removed post's replies
/// stay; each is reported on its own.
/// </para>
/// </remarks>
public sealed class Reports(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IPortfolioModeration? portfolios = null)
{
    private readonly ReportTargets _targets = new(db, tenant, portfolios);

    public const string Made = "reports.made";
    public const string Read = "reports.read";
    public const string Acknowledged = "reports.acknowledged";
    public const string Dismissed = "reports.dismissed";
    public const string Removed = "reports.message_removed";
    public const string PostRemoved = "reports.post_removed";
    public const string ShowcaseItemRemoved = "reports.showcase_item_removed";
    public const string SubmissionRemoved = "reports.submission_removed";
    public const string PortfolioRemoved = "reports.portfolio_removed";
    public const string Escalated = "reports.escalated";
    public const string EscalationsRead = "reports.escalated_read";
    public const string EscalationClosed = "reports.escalation_closed";

    public const int MaxDetails = 2000;
    public const int MaxNote = 1000;
    public const int PageSize = 100;

    /// <summary>
    /// Who is told of a critical report: <c>Permissions.Moderation.Triage</c> in Identity, which
    /// this module can't reference.
    /// </summary>
    public const string ModeratorPermission = "moderation.triage";

    /// <summary>A person in the conversation reports a message someone else wrote in it.</summary>
    public Task<Result<ReportReceipt>> ReportMessageAsync(UserId reporter, Guid messageId, string? reason, string? details, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Invalid(reason, details, "message") is { } invalid)
            {
                return Result<ReportReceipt>.Failure(invalid);
            }

            // Only a message the reporter can see: sent, someone else's, in a conversation they're in.
            ReportedItem? message = await _targets.FindAsync(ReportTargets.Message, messageId, ct).ConfigureAwait(false);
            if (message is null || !ReportTargets.IsUp(message) || message.AuthorUserId == reporter.Value
                || !await db.Participants.AsNoTracking()
                    .AnyAsync(p => p.ConversationId == message.Container && p.UserId == reporter.Value && p.RemovedAt == null, ct).ConfigureAwait(false))
            {
                return Result<ReportReceipt>.Failure(Error.NotFound("reports.not_found", "There's no message like that in your conversations."));
            }

            return Result<ReportReceipt>.Success(await MakeAsync(reporter, message, reason!, details, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>A member of a space reports a published post someone else wrote in it (D121).</summary>
    public Task<Result<ReportReceipt>> ReportPostAsync(UserId reporter, Guid postId, string? reason, string? details, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Invalid(reason, details, "post") is { } invalid)
            {
                return Result<ReportReceipt>.Failure(invalid);
            }

            // Only a post the reporter can see: published, someone else's, in a space they're in.
            ReportedItem? post = await _targets.FindAsync(ReportTargets.Post, postId, ct).ConfigureAwait(false);
            if (post is null || !ReportTargets.IsUp(post) || post.AuthorUserId == reporter.Value
                || !await db.SpaceMembers.AsNoTracking()
                    .AnyAsync(m => m.SpaceId == post.Container && m.UserId == reporter.Value && m.LeftAt == null, ct).ConfigureAwait(false))
            {
                return Result<ReportReceipt>.Failure(Error.NotFound("reports.post_not_found", "There's no post like that in your spaces."));
            }

            return Result<ReportReceipt>.Success(await MakeAsync(reporter, post, reason!, details, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// One of the organisation's staff reports a young person's work for a challenge in one of its
    /// spaces (D128), as sent: a worry about what it says reaches the county. The caller has checked
    /// that they may see the organisation's spaces.
    /// </summary>
    public Task<Result<ReportReceipt>> ReportSubmissionAsync(UserId reporter, OrganisationId org, Guid submissionId, string? reason, string? details, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Invalid(reason, details, "work") is { } invalid)
            {
                return Result<ReportReceipt>.Failure(invalid);
            }

            ReportedItem? work = await _targets.FindAsync(ReportTargets.Submission, submissionId, ct).ConfigureAwait(false);
            if (work is null || work.OrgId != org.Value || !ReportTargets.IsUp(work) || work.AuthorUserId == reporter.Value)
            {
                return Result<ReportReceipt>.Failure(Error.NotFound("reports.work_not_found", "There's no work like that sent in your organisation's spaces."));
            }

            return Result<ReportReceipt>.Success(await MakeAsync(reporter, work, reason!, details, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// Anyone reports an item on the public showcase (D128): signed in, as themselves; not signed
    /// in (<paramref name="reporter"/> null), with no reporter recorded. The host limits how often.
    /// </summary>
    public Task<Result<ReportReceipt>> ReportShowcaseItemAsync(UserId? reporter, Guid itemId, string? reason, string? details, CancellationToken cancellationToken = default) =>
        PublicAsync(reporter, ReportTargets.ShowcaseItem, itemId, reason, details, "showcase.not_shown", "There's nothing like that on the showcase.", cancellationToken);

    /// <summary>Anyone reports a shown portfolio, by its link name (D128), as <see cref="ReportShowcaseItemAsync"/> does an item.</summary>
    public async Task<Result<ReportReceipt>> ReportPortfolioAsync(UserId? reporter, string? slug, string? reason, string? details, CancellationToken cancellationToken = default)
    {
        ReportablePortfolio? page = portfolios is { } shown ? await shown.ShownAsync(slug ?? "", cancellationToken).ConfigureAwait(false) : null;
        return page is null
            ? Result<ReportReceipt>.Failure(Error.NotFound("portfolio.not_shown", "There's no portfolio shown at that link."))
            : await PublicAsync(reporter, ReportTargets.Portfolio, page.UserId, reason, details, "portfolio.not_shown", "There's no portfolio shown at that link.", cancellationToken).ConfigureAwait(false);
    }

    private Task<Result<ReportReceipt>> PublicAsync(
        UserId? reporter, string kind, Guid id, string? reason, string? details, string notFoundCode, string notFound, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (Invalid(reason, details, kind == ReportTargets.Portfolio ? "page" : "item") is { } invalid)
            {
                return Result<ReportReceipt>.Failure(invalid);
            }

            // Signed in: as any report, once per person while it waits, and never of their own.
            if (reporter is { } signedIn)
            {
                ReportedItem? item = await _targets.FindAsync(kind, id, ct).ConfigureAwait(false);
                return item is null || !ReportTargets.IsUp(item) || item.AuthorUserId == signedIn.Value
                    ? Result<ReportReceipt>.Failure(Error.NotFound(notFoundCode, notFound))
                    : Result<ReportReceipt>.Success(await MakeAsync(signedIn, item, reason!, details, ct).ConfigureAwait(false));
            }

            // Not signed in: reports sit behind the actor guard, so it's filed by a definer function,
            // which checks the item is shown now and says whose organisation it is.
            Guid reportId = UuidV7.NewId();
            string severity = ReportReasons.SeverityOf(reason!);
            string? said = details?.Trim() is { Length: > 0 } d ? d : null;
            Guid tenantId = tenant.TenantId.Value;
            Guid? org = await db.Database
                .SqlQuery<Guid?>($"select community.file_public_report({tenantId}, {reportId}, {kind}, {id}, {reason}, {said}, {severity}) as \"Value\"")
                .FirstAsync(ct).ConfigureAwait(false);
            if (org is not { } owner)
            {
                return Result<ReportReceipt>.Failure(Error.NotFound(notFoundCode, notFound));
            }

            if (severity == ReportSeverities.Critical)
            {
                CriticalPublicReport critical = new(reportId, reason!, owner);
                await db.EnqueueJobAsync(new JobRequest
                {
                    JobType = CriticalPublicReport.JobType,
                    TenantId = tenant.TenantId,
                    Payload = critical,
                    UniqueKey = critical.UniqueKey,
                }, ct).ConfigureAwait(false);
            }

            await AuditAsync(Made, kind, [id], ct).ConfigureAwait(false);
            return Result<ReportReceipt>.Success(new ReportReceipt(reportId, reason!, severity));
        }, cancellationToken);

    private static Error? Invalid(string? reason, string? details, string what)
    {
        if (reason is null || !ReportReasons.ForMessages.Contains(reason))
        {
            return Error.Validation("reports.reason", $"Choose what's wrong with the {what}.");
        }

        return details?.Trim() is { Length: > MaxDetails }
            ? Error.Validation("reports.details", $"Say what happened in up to {MaxDetails} characters.")
            : null;
    }

    /// <summary>Files the report, or finds the reporter's own still waiting; a critical one emails the moderators who may decide it.</summary>
    private async Task<ReportReceipt> MakeAsync(UserId reporter, ReportedItem item, string reason, string? details, CancellationToken ct)
    {
        if (await db.Reports.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TargetKind == item.Kind && r.TargetId == item.Id && r.ReporterUserId == reporter.Value
                    && (r.State == ReportStates.Open || r.State == ReportStates.Triaged), ct).ConfigureAwait(false) is { } waiting)
        {
            return new ReportReceipt(waiting.Id, waiting.ReasonCode, waiting.Severity);
        }

        ReportRecord report = new()
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            TargetKind = item.Kind,
            TargetId = item.Id,
            ReporterUserId = reporter.Value,
            ReporterIsMinor = await identity.IsMinorAsync(reporter, ct).ConfigureAwait(false),
            ReasonCode = reason,
            Details = details?.Trim() is { Length: > 0 } said ? said : null,
            Severity = ReportReasons.SeverityOf(reason),
            State = ReportStates.Open,
            CreatedAt = clock.StoredNow(),
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await NotifyIfCriticalAsync(report.Id, reason, report.Severity, item.OrgId, item.AuthorUserId, reporter.Value, ct).ConfigureAwait(false);
        await AuditAsync(Made, item.Kind, [item.Id], ct).ConfigureAwait(false);
        return new ReportReceipt(report.Id, report.ReasonCode, report.Severity);
    }

    /// <summary>
    /// A critical report books an email to every moderator who may decide it: not the reporter, the
    /// author, or a member of the organisation it's about (a portfolio is of none).
    /// </summary>
    private async Task NotifyIfCriticalAsync(Guid reportId, string reason, string severity, Guid orgId, Guid? author, Guid? reporter, CancellationToken ct)
    {
        if (severity != ReportSeverities.Critical)
        {
            return;
        }

        IReadOnlyList<OrganisationMembership> members = orgId == Guid.Empty
            ? []
            : await directory.GetOrganisationMembersAsync(new OrganisationId(orgId), ct).ConfigureAwait(false);
        foreach (Guid moderator in await identity.GetUsersWithPermissionAsync(ModeratorPermission, ct).ConfigureAwait(false))
        {
            if (moderator == reporter || moderator == author || members.Any(m => m.UserId.Value == moderator))
            {
                continue;
            }

            CriticalReportNotice notice = new(moderator, reportId, reason);
            await db.EnqueueJobAsync(new JobRequest
            {
                JobType = CriticalReportNotice.JobType,
                TenantId = tenant.TenantId,
                Payload = notice,
                UniqueKey = notice.UniqueKey,
            }, ct).ConfigureAwait(false);
        }
    }

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
                if (await _targets.FindAsync(report.TargetKind, report.TargetId, ct).ConfigureAwait(false) is not { } item)
                {
                    continue;
                }

                (string Name, bool Mine) org = await OrganisationAsync(orgs, item.OrgId, moderator, ct).ConfigureAwait(false);
                list.Add(new QueuedReport(
                    report.Id,
                    report.ReasonCode,
                    report.Severity,
                    report.State,
                    item.ReporterRole(report.ReporterUserId),
                    report.Details,
                    report.CreatedAt,
                    report.AcknowledgedAt,
                    report.Severity == ReportSeverities.Critical && report.AcknowledgedAt is null && now - report.CreatedAt > ReportSeverities.CriticalAcknowledgement,
                    org.Mine || report.ReporterUserId == moderator.Value || item.AuthorUserId == moderator.Value,
                    item.Kind,
                    item.Id,
                    org.Name,
                    item.Where,
                    item.AuthorRole,
                    item.Body,
                    item.State,
                    item.At,
                    await _targets.EarlierAsync(item, ct).ConfigureAwait(false)));
            }

            await AuditAsync(Read, "report", [.. list.Select(r => r.Id)], ct).ConfigureAwait(false);
            return list;
        }, cancellationToken);

    /// <summary>
    /// The escalated reports still waiting for their outcome (D116), oldest first: each stays here
    /// until a moderator records how it ended. Reading them is audited.
    /// </summary>
    public Task<IReadOnlyList<EscalatedReport>> EscalatedAsync(UserId moderator, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<EscalatedReport>>(async ct =>
        {
            List<ReportRecord> escalated = await db.Reports.AsNoTracking()
                .Where(r => r.State == ReportStates.Escalated)
                .OrderBy(r => r.EscalatedAt).ThenBy(r => r.Id)
                .Take(PageSize).ToListAsync(ct).ConfigureAwait(false);
            Dictionary<Guid, (string Name, bool Mine)> orgs = [];
            List<EscalatedReport> list = [];
            foreach (ReportRecord report in escalated)
            {
                if (await _targets.FindAsync(report.TargetKind, report.TargetId, ct).ConfigureAwait(false) is not { } item)
                {
                    continue;
                }

                (string Name, bool Mine) org = await OrganisationAsync(orgs, item.OrgId, moderator, ct).ConfigureAwait(false);
                list.Add(new EscalatedReport(report.Id, report.ReasonCode, report.Severity, report.EscalatedAt ?? report.CreatedAt, report.EscalationNote,
                    item.Kind, item.Id, org.Name, org.Mine || report.ReporterUserId == moderator.Value || item.AuthorUserId == moderator.Value));
            }

            await AuditAsync(EscalationsRead, "report", [.. list.Select(r => r.Id)], ct).ConfigureAwait(false);
            return list;
        }, cancellationToken);

    /// <summary>
    /// Records how an escalation ended (D116), such as the agency's reference and what they said:
    /// the report is closed as actioned with the outcome, and the decision is a
    /// <c>moderation_action</c> (<c>no_action</c>: nothing more for BrightTime to do).
    /// </summary>
    public Task<Result> CloseEscalationAsync(UserId moderator, Guid reportId, string? outcome, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (outcome?.Trim() is not { Length: > 0 and <= MaxNote } said)
            {
                return Result.Failure(Error.Validation("reports.outcome", $"Write how it ended in up to {MaxNote} characters; it's kept in the moderation record."));
            }

            ReportRecord? report = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId && r.State == ReportStates.Escalated, ct).ConfigureAwait(false);
            ReportedItem? item = report is null ? null : await _targets.FindAsync(report.TargetKind, report.TargetId, ct).ConfigureAwait(false);
            if (report is null || item is null)
            {
                return Result.Failure(Error.NotFound("reports.not_escalated", "That report isn't waiting for its outcome any more."));
            }

            if (await OwnAsync(moderator, report, item, ct).ConfigureAwait(false))
            {
                return Result.Failure(OwnOrganisation());
            }

            DateTimeOffset now = clock.StoredNow();
            int changed = await db.Reports.Where(r => r.Id == reportId && r.State == ReportStates.Escalated)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, ReportStates.Actioned)
                    .SetProperty(r => r.ResolvedAt, now).SetProperty(r => r.Resolution, said), ct).ConfigureAwait(false);
            if (changed == 0)
            {
                return Result.Failure(Error.NotFound("reports.not_escalated", "That report isn't waiting for its outcome any more."));
            }

            Act(report, "no_action", moderator, said, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(EscalationClosed, "report", [reportId], ct).ConfigureAwait(false);
            return Result.Success();
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

    /// <summary>Nothing to do: the report is closed with the moderator's note, and what was reported stays.</summary>
    public Task<Result> DismissAsync(UserId moderator, Guid reportId, string? note, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, reportId, note, Dismissed, removeIt: false, cancellationToken);

    /// <summary>What was reported is removed, and every report of it waiting is settled with the note.</summary>
    public Task<Result> RemoveAsync(UserId moderator, Guid reportId, string? note, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, reportId, note, Removed, removeIt: true, cancellationToken);

    /// <summary>
    /// The mandated-reporter hand-off: the report is escalated with the moderator's note of what was
    /// done and who was told, and what was reported is removed too when <paramref name="removeIt"/>.
    /// </summary>
    public Task<Result> EscalateAsync(UserId moderator, Guid reportId, string? note, bool removeIt, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, reportId, note, Escalated, removeIt, cancellationToken);

    /// <summary>
    /// A decision on a waiting report: <see cref="Dismissed"/>, <see cref="Removed"/> or
    /// <see cref="Escalated"/>; what was reported taken down when asked.
    /// </summary>
    private Task<Result> DecideAsync(UserId moderator, Guid reportId, string? note, string decision, bool removeIt, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (note?.Trim() is not { Length: > 0 and <= MaxNote } why)
            {
                return Result.Failure(Error.Validation("reports.note", $"Write a note of up to {MaxNote} characters; it's kept in the moderation record."));
            }

            (ReportRecord? report, ReportedItem? item, Error? refused) = await DecidableAsync(moderator, reportId, ct).ConfigureAwait(false);
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

                // It goes, so every report of it waiting is settled with it.
                Removed => await waiting.Where(r => r.TargetKind == decided.TargetKind && r.TargetId == decided.TargetId)
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
            if (removeIt)
            {
                await _targets.TakeDownAsync(item!, moderator.Value, decided.Id, now, ct).ConfigureAwait(false);
                if (decision == Escalated)
                {
                    Act(decided, "remove", moderator, why, now);
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(decision == Removed && item!.Kind != ReportTargets.Message ? RemovedOf(item.Kind) : decision, "report", [reportId], ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The report and what it's about, if it's waiting and this moderator may decide it.</summary>
    private async Task<(ReportRecord? Report, ReportedItem? Item, Error? Refused)> DecidableAsync(UserId moderator, Guid reportId, CancellationToken ct)
    {
        ReportRecord? report = await db.Reports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && (r.State == ReportStates.Open || r.State == ReportStates.Triaged), ct).ConfigureAwait(false);
        ReportedItem? item = report is null ? null : await _targets.FindAsync(report.TargetKind, report.TargetId, ct).ConfigureAwait(false);
        if (report is null || item is null)
        {
            return (null, null, NotWaiting());
        }

        return await OwnAsync(moderator, report, item, ct).ConfigureAwait(false) ? (null, null, OwnOrganisation()) : (report, item, null);
    }

    /// <summary>
    /// Whether the report is the moderator's own business: their organisation's conversation or
    /// space, their report, or what they wrote.
    /// </summary>
    private async Task<bool> OwnAsync(UserId moderator, ReportRecord report, ReportedItem item, CancellationToken ct) =>
        report.ReporterUserId == moderator.Value || item.AuthorUserId == moderator.Value
        || (item.OrgId != Guid.Empty
            && (await directory.GetOrganisationMembersAsync(new OrganisationId(item.OrgId), ct).ConfigureAwait(false)).Any(m => m.UserId == moderator));

    /// <summary>The audit action of removing what was reported, by its kind (a message's is <see cref="Removed"/>).</summary>
    private static string RemovedOf(string kind) => kind switch
    {
        ReportTargets.Post => PostRemoved,
        ReportTargets.ShowcaseItem => ShowcaseItemRemoved,
        ReportTargets.Submission => SubmissionRemoved,
        ReportTargets.Portfolio => PortfolioRemoved,
        _ => Removed,
    };

    private async Task<(string Name, bool Mine)> OrganisationAsync(Dictionary<Guid, (string Name, bool Mine)> seen, Guid orgId, UserId moderator, CancellationToken ct)
    {
        if (orgId == Guid.Empty)
        {
            return ("", false);
        }

        if (!seen.TryGetValue(orgId, out (string Name, bool Mine) org))
        {
            OrganisationId id = new(orgId);
            org = (await directory.GetOrganisationNameAsync(id, ct).ConfigureAwait(false) ?? "",
                (await directory.GetOrganisationMembersAsync(id, ct).ConfigureAwait(false)).Any(m => m.UserId == moderator));
            seen[orgId] = org;
        }

        return org;
    }

    private static Error OwnOrganisation() => Error.Forbidden("reports.own_organisation",
        "You're in the organisation it was written in, or it's your own report or what you wrote, so another moderator decides it.");

    private void Act(ReportRecord report, string action, UserId moderator, string reason, DateTimeOffset at) =>
        db.ModerationActions.Add(new ModerationActionRecord
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            ReportId = report.Id,
            TargetKind = report.TargetKind,
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
