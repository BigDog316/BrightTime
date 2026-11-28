using BrightTime.Abstractions.Screening;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Showcasing;

/// <summary>What an item on the showcase is of (D124).</summary>
public static class ShowcaseKinds
{
    /// <summary>A project staff approved (D69).</summary>
    public const string Project = "project";

    /// <summary>Work for a challenge staff accepted (D122).</summary>
    public const string Submission = "submission";
}

/// <summary>Where an item stands, derived from its times.</summary>
public static class ShowcaseStates
{
    /// <summary>Proposed by staff, waiting for its author's answer.</summary>
    public const string Waiting = "waiting";

    /// <summary>On the showcase.</summary>
    public const string Shown = "shown";

    /// <summary>Its author said no.</summary>
    public const string Declined = "declined";

    /// <summary>Taken off the showcase (or withdrawn before an answer), by its author, the organisation or the county.</summary>
    public const string TakenDown = "taken_down";
}

/// <summary>Work an organisation's staff could propose for the showcase: accepted challenge work, or an approved project.</summary>
[YouthData]
public sealed record ShowcaseCandidate(string Kind, Guid Id, string Title, string Body, string? AuthorName, string? Where, DateTimeOffset At);

/// <summary>
/// An item on, or proposed for, the showcase. <see cref="AuthorName"/> is the portfolio display
/// name, for staff; <see cref="DisplayName"/> the name it's shown under, once its author said yes.
/// <see cref="EndedByAuthor"/> says whether a decline or take-down was its author's own.
/// </summary>
[YouthData]
public sealed record ShowcaseEntry(
    Guid Id,
    string Kind,
    string Title,
    string Body,
    string? Caption,
    string OrgName,
    string State,
    string? DisplayName,
    string? AuthorName,
    DateTimeOffset ProposedAt,
    DateTimeOffset? ShownAt,
    DateTimeOffset? EndedAt,
    bool EndedByAuthor,
    string? Reason);

/// <summary>An item as the public showcase shows it: the name its author chose, never an account or a legal name.</summary>
[YouthData]
public sealed record ShownWork(Guid Id, string DisplayName, string Title, string Body, string? Caption, string OrgName, string Kind, DateTimeOffset ShownAt);

/// <summary>
/// The site's showcase of young people's work (M5, D124). An organisation's staff propose work of
/// theirs (challenge work they accepted, or a project they approved), with a caption; it goes on
/// the public showcase only when its author says yes, under the name they chose for their portfolio,
/// and only while they may publish to the showcase: nobody under 13, and a guardian's showcase
/// consent where a guardian decides (D4). Its author, the organisation's staff and the county take
/// it down at any time.
/// </summary>
/// <remarks>
/// <para>
/// What's shown is the work as it was approved: its title and words are kept on the item. It goes
/// off the showcase by itself if the author can no longer publish there (consent withdrawn, an
/// account suspended), if the project goes back to a draft or is removed, or if the work is no
/// longer accepted. The public page reads it through <c>community.public_showcase()</c>, since the
/// items sit behind the actor guard.
/// </para>
/// <para>
/// One item per piece of work, ever: work its author declined isn't asked about again, and a
/// take-down is for good. A caption is screened for personal information (D117) and refused, with
/// why, if it has any.
/// </para>
/// </remarks>
public sealed class Showcase(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IShowcaseWork work,
    IScreeningService screener)
{
    public const string Read = "showcase.read";
    public const string Proposed = "showcase.proposed";
    public const string Published = "showcase.published";
    public const string Declined = "showcase.declined";
    public const string TakenDown = "showcase.taken_down";
    public const string PublicRead = "showcase.public_read";

    /// <summary>The notice's kind (<c>ck_notification_kind</c>), which the young person can switch off (D126).</summary>
    public const string RequestKind = "showcase_request";

    /// <summary>The notice's words: the organisation's name, never the work's title or words, which a locked phone would show.</summary>
    public const string RequestTitle = "notify.showcase.proposed";

    public const int MaxCaption = 300;
    public const int MaxReason = 500;
    public const int PageSize = 24;

    /// <summary>The organisation's work that could be proposed: accepted challenge work and approved projects not proposed before.</summary>
    public Task<IReadOnlyList<ShowcaseCandidate>> CandidatesAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ShowcaseCandidate>>(async ct =>
        {
            var accepted = await (from s in db.Submissions.AsNoTracking()
                                  join c in db.Challenges on s.ChallengeId equals c.Id
                                  join sp in db.Spaces on c.SpaceId equals sp.Id
                                  where sp.OrgId == org.Value && s.State == SubmissionStates.Accepted
                                        && !db.ShowcaseItems.Any(i => i.SubmissionId == s.Id)
                                  orderby s.UpdatedAt descending
                                  select new { s.Id, s.AuthorUserId, c.Title, s.Body, Space = sp.Name, s.UpdatedAt })
                .Take(200).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> proposed = await db.ShowcaseItems.AsNoTracking().Where(i => i.OrgId == org.Value && i.ProjectId != null)
                .Select(i => i.ProjectId!.Value).ToListAsync(ct).ConfigureAwait(false);
            List<ApprovedProject> projects = [.. (await work.ApprovedProjectsAsync(org, ct).ConfigureAwait(false)).Where(p => !proposed.Contains(p.Id))];
            IReadOnlyDictionary<Guid, string> names = await work.DisplayNamesAsync([.. accepted.Select(a => a.AuthorUserId)], ct).ConfigureAwait(false);

            await AuditAsync(Read, "submission", [.. accepted.Select(a => a.Id)], org, ct).ConfigureAwait(false);
            await AuditAsync(Read, "project", [.. projects.Select(p => p.Id)], org, ct).ConfigureAwait(false);
            return [
                .. accepted.Select(a => new ShowcaseCandidate(ShowcaseKinds.Submission, a.Id, a.Title, a.Body ?? "", names.GetValueOrDefault(a.AuthorUserId), a.Space, a.UpdatedAt)),
                .. projects.Select(p => new ShowcaseCandidate(ShowcaseKinds.Project, p.Id, p.Title, p.Description ?? "", p.AuthorName, p.Activity, p.ApprovedAt)),
            ];
        }, cancellationToken);

    /// <summary>The organisation's items, newest first, in every state.</summary>
    public Task<IReadOnlyList<ShowcaseEntry>> ForOrganisationAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ShowcaseEntry>>(async ct =>
        {
            List<ShowcaseItemRecord> items = await db.ShowcaseItems.AsNoTracking().Where(i => i.OrgId == org.Value)
                .OrderByDescending(i => i.ApprovedAt).ThenByDescending(i => i.Id).Take(500).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<ShowcaseEntry> entries = await EntriesAsync(items, forStaff: true, ct).ConfigureAwait(false);
            await AuditAsync(Read, "showcase_item", [.. items.Select(i => i.Id)], org, ct).ConfigureAwait(false);
            return entries;
        }, cancellationToken);

    /// <summary>What's on the county's showcase now, and what was taken down lately, for its organisers and moderators.</summary>
    public Task<IReadOnlyList<ShowcaseEntry>> ForCountyAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ShowcaseEntry>>(async ct =>
        {
            List<ShowcaseItemRecord> items = await db.ShowcaseItems.AsNoTracking().Where(i => i.PublishedAt != null)
                .OrderByDescending(i => i.UnpublishedAt == null).ThenByDescending(i => i.PublishedAt).ThenByDescending(i => i.Id)
                .Take(500).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<ShowcaseEntry> entries = await EntriesAsync(items, forStaff: true, ct).ConfigureAwait(false);
            await AuditAsync(Read, "showcase_item", [.. items.Select(i => i.Id)], null, ct).ConfigureAwait(false);
            return entries;
        }, cancellationToken);

    /// <summary>
    /// One of the organisation's staff proposes work for the showcase, with a caption if they like.
    /// The caller has checked <c>showcase.approve</c> for the organisation.
    /// </summary>
    public Task<Result<Guid>> ProposeAsync(UserId staff, OrganisationId org, string? kind, Guid id, string? caption, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? said = caption?.Trim() is { Length: > 0 } c ? c : null;
            if (said is { Length: > MaxCaption })
            {
                return Result<Guid>.Failure(Error.Validation("showcase.caption", $"A caption of up to {MaxCaption} characters, or none."));
            }

            (Guid author, string title, string body)? source = kind switch
            {
                ShowcaseKinds.Submission => await (from s in db.Submissions.AsNoTracking()
                                                   join ch in db.Challenges on s.ChallengeId equals ch.Id
                                                   join sp in db.Spaces on ch.SpaceId equals sp.Id
                                                   where s.Id == id && sp.OrgId == org.Value && s.State == SubmissionStates.Accepted
                                                   select new { s.AuthorUserId, ch.Title, s.Body })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } w ? (w.AuthorUserId, w.Title, w.Body ?? "") : null,
                ShowcaseKinds.Project => await work.ApprovedProjectAsync(org, id, ct).ConfigureAwait(false) is { } p ? (p.AuthorUserId, p.Title, p.Description ?? "") : null,
                _ => null,
            };
            if (source is not { } found)
            {
                return Result<Guid>.Failure(Error.NotFound("showcase.not_found", "There's no accepted work or approved project of your organisation like that."));
            }

            bool asked = kind == ShowcaseKinds.Project
                ? await db.ShowcaseItems.AnyAsync(i => i.ProjectId == id, ct).ConfigureAwait(false)
                : await db.ShowcaseItems.AnyAsync(i => i.SubmissionId == id, ct).ConfigureAwait(false);
            if (asked)
            {
                return Result<Guid>.Failure(Error.Conflict("showcase.already", "This work has been asked about already. Each piece is asked about once."));
            }

            if (said is not null
                && await ContentScreening.ScreenAsync(screener, said, MessageScreen.YouthPii, audienceIncludesMinor: true, "showcase", ct).ConfigureAwait(false) is { Passed: false } verdict)
            {
                return Result<Guid>.Failure(Error.Validation("showcase.screened",
                    $"The caption looks like it shares personal information ({string.Join(", ", verdict.Reasons)}). Take that out and try again."));
            }

            DateTimeOffset now = clock.StoredNow();
            ShowcaseItemRecord item = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                AuthorUserId = found.author,
                ProjectId = kind == ShowcaseKinds.Project ? id : null,
                SubmissionId = kind == ShowcaseKinds.Submission ? id : null,
                Title = found.title.Length > 200 ? found.title[..200] : found.title,
                Body = found.body,
                OrgName = await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "",
                Caption = said,
                ApprovedBy = staff.Value,
                ApprovedAt = now,
            };
            db.ShowcaseItems.Add(item);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Proposed, "showcase_item", [item.Id], org, ct, (kind!, id.ToString("D"))).ConfigureAwait(false);

            // Told with the proposal, through the outbox, so a proposal that fails tells nobody (D126).
            await db.PublishAsync(tenant.TenantId, new NotificationRequested(
                found.author,
                RequestKind,
                RequestTitle,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["org"] = item.OrgName },
                "/portfolio/showcase",
                "showcase-proposed:" + item.Id.ToString("N")), ct).ConfigureAwait(false);
            return Result<Guid>.Success(item.Id);
        }, cancellationToken);

    /// <summary>
    /// Takes an item off the showcase, or withdraws it before its author answered: one of the
    /// organisation's staff (<paramref name="org"/>), or the county's organisers and moderators (null).
    /// The caller has checked <c>showcase.approve</c>.
    /// </summary>
    public Task<Result> TakeDownAsync(UserId actor, OrganisationId? org, Guid itemId, string? reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? why = reason?.Trim() is { Length: > 0 } r ? r : null;
            if (why is { Length: > MaxReason })
            {
                return Result.Failure(Error.Validation("showcase.reason", $"A reason of up to {MaxReason} characters, or none."));
            }

            ShowcaseItemRecord? item = await db.ShowcaseItems.FirstOrDefaultAsync(i => i.Id == itemId && (org == null || i.OrgId == org.Value.Value), ct).ConfigureAwait(false);
            if (item is null)
            {
                return Result.Failure(ItemNotFound());
            }

            if (item.UnpublishedAt is not null)
            {
                return Result.Failure(Error.Conflict("showcase.ended", "This has been taken down already."));
            }

            item.UnpublishedAt = clock.StoredNow();
            item.UnpublishedBy = actor.Value;
            item.UnpublishReason = why;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(TakenDown, "showcase_item", [item.Id], org, ct, ("by", org is null ? "county" : "organisation")).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>A young person's own items: what's waiting for their answer, what's shown, and what's ended.</summary>
    public Task<IReadOnlyList<ShowcaseEntry>> MineAsync(UserId youth, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ShowcaseEntry>>(async ct =>
        {
            List<ShowcaseItemRecord> items = await db.ShowcaseItems.AsNoTracking().Where(i => i.AuthorUserId == youth.Value)
                .OrderByDescending(i => i.ApprovedAt).ThenByDescending(i => i.Id).Take(200).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<ShowcaseEntry> entries = await EntriesAsync(items, forStaff: false, ct).ConfigureAwait(false);
            await AuditAsync(Read, "showcase_item", [.. items.Select(i => i.Id)], null, ct).ConfigureAwait(false);
            return entries;
        }, cancellationToken);

    /// <summary>
    /// A young person answers a proposal: yes puts it on the showcase under their portfolio's name,
    /// if they may publish there; no declines it, for good.
    /// </summary>
    public Task<Result<ShowcaseEntry>> AnswerAsync(UserId youth, Guid itemId, bool show, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ShowcaseItemRecord? item = await db.ShowcaseItems.FirstOrDefaultAsync(i => i.Id == itemId && i.AuthorUserId == youth.Value, ct).ConfigureAwait(false);
            if (item is null)
            {
                return Result<ShowcaseEntry>.Failure(ItemNotFound());
            }

            if (item.PublishedAt is not null || item.UnpublishedAt is not null)
            {
                return Result<ShowcaseEntry>.Failure(Error.Conflict("showcase.answered", "You've answered this already."));
            }

            DateTimeOffset now = clock.StoredNow();
            if (!show)
            {
                item.UnpublishedAt = now;
                item.UnpublishedBy = youth.Value;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await AuditAsync(Declined, "showcase_item", [item.Id], null, ct).ConfigureAwait(false);
                return Result<ShowcaseEntry>.Success((await EntriesAsync([item], forStaff: false, ct).ConfigureAwait(false))[0]);
            }

            if (!(await identity.GetCapabilitiesAsync(youth, ct).ConfigureAwait(false)).CanPublishShowcase)
            {
                return Result<ShowcaseEntry>.Failure(Error.Forbidden("showcase.not_open", "Showing your work in the showcase isn't open to you yet."));
            }

            ConsentBasis basis = await identity.GetConsentBasisAsync(youth, ConsentScope.ShowcasePublication, ct).ConfigureAwait(false);
            if (basis.GuardianManaged && basis.ConsentId is null)
            {
                return Result<ShowcaseEntry>.Failure(Error.Forbidden("showcase.not_open", "Showing your work in the showcase isn't open to you yet."));
            }

            ShowcaseName name = await work.ShowcaseNameAsync(youth, ct).ConfigureAwait(false);
            if (name.DisplayName is null)
            {
                return Result<ShowcaseEntry>.Failure(Error.Validation("showcase.display_name", "Choose the name your portfolio shows first, in Who sees your portfolio."));
            }

            if (name.CarriesFamilyName)
            {
                return Result<ShowcaseEntry>.Failure(Error.Validation("showcase.display_name_legal", "Your portfolio's name has your family name in it. Choose one without it first."));
            }

            if (!await StillApprovedAsync(item, ct).ConfigureAwait(false))
            {
                return Result<ShowcaseEntry>.Failure(Error.Conflict("showcase.changed", "This work has changed since it was chosen, so it can't be shown as it was."));
            }

            item.DisplayName = name.DisplayName;
            item.GuardianManaged = basis.GuardianManaged;
            item.GuardianConsentId = basis.GuardianManaged ? basis.ConsentId : null;
            item.PublishedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Published, "showcase_item", [item.Id], null, ct, ("consent", basis.GuardianManaged ? "guardian" : "own")).ConfigureAwait(false);
            return Result<ShowcaseEntry>.Success((await EntriesAsync([item], forStaff: false, ct).ConfigureAwait(false))[0]);
        }, cancellationToken);

    /// <summary>A young person takes their own item down, shown or still waiting: for good.</summary>
    public Task<Result> TakeDownMineAsync(UserId youth, Guid itemId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ShowcaseItemRecord? item = await db.ShowcaseItems.FirstOrDefaultAsync(i => i.Id == itemId && i.AuthorUserId == youth.Value, ct).ConfigureAwait(false);
            if (item is null)
            {
                return Result.Failure(ItemNotFound());
            }

            if (item.UnpublishedAt is not null)
            {
                return Result.Failure(Error.Conflict("showcase.ended", "This has been taken down already."));
            }

            item.UnpublishedAt = clock.StoredNow();
            item.UnpublishedBy = youth.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(TakenDown, "showcase_item", [item.Id], null, ct, ("by", "author")).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The public showcase, newest first, a page at a time: items shown before <paramref name="before"/>, if given.</summary>
    public Task<IReadOnlyList<ShownWork>> ShownAsync(DateTimeOffset? before, int limit = PageSize, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ShownWork>>(async ct =>
        {
            Guid tenantId = tenant.TenantId.Value;
            int take = Math.Clamp(limit, 1, 100);
            List<ShownWork> shown = await db.Database
                .SqlQuery<ShownWork>(
                    $"""
                    select id as "Id", display_name as "DisplayName", title as "Title", body as "Body", caption as "Caption",
                           org_name as "OrgName", kind as "Kind", published_at as "ShownAt"
                      from community.public_showcase({tenantId}, {before}, {take})
                    """)
                .ToListAsync(ct).ConfigureAwait(false);
            await AuditAsync(PublicRead, "showcase_item", [.. shown.Select(s => s.Id)], null, ct).ConfigureAwait(false);
            return shown;
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<bool> StillApprovedAsync(ShowcaseItemRecord item, CancellationToken ct) =>
        item.SubmissionId is { } submission
            ? await db.Submissions.AsNoTracking().AnyAsync(s => s.Id == submission && s.State == SubmissionStates.Accepted, ct).ConfigureAwait(false)
            : item.ProjectId is { } project && await work.ApprovedProjectAsync(new OrganisationId(item.OrgId), project, ct).ConfigureAwait(false) is not null;

    private async Task<IReadOnlyList<ShowcaseEntry>> EntriesAsync(IReadOnlyList<ShowcaseItemRecord> items, bool forStaff, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, string> names = forStaff
            ? await work.DisplayNamesAsync([.. items.Select(i => i.AuthorUserId)], ct).ConfigureAwait(false)
            : new Dictionary<Guid, string>();
        return [.. items.Select(i => new ShowcaseEntry(
            i.Id,
            i.ProjectId is null ? ShowcaseKinds.Submission : ShowcaseKinds.Project,
            i.Title,
            i.Body,
            i.Caption,
            i.OrgName,
            StateOf(i),
            i.DisplayName,
            forStaff ? names.GetValueOrDefault(i.AuthorUserId) : null,
            i.ApprovedAt,
            i.PublishedAt,
            i.UnpublishedAt,
            i.UnpublishedBy == i.AuthorUserId,
            // Why staff or the county took it down is for staff; its author is told it was taken down.
            forStaff ? i.UnpublishReason : null))];
    }

    private static string StateOf(ShowcaseItemRecord item) =>
        item.UnpublishedAt is not null
            ? item.PublishedAt is null && item.UnpublishedBy == item.AuthorUserId ? ShowcaseStates.Declined : ShowcaseStates.TakenDown
            : item.PublishedAt is not null ? ShowcaseStates.Shown : ShowcaseStates.Waiting;

    private Task AuditAsync(string action, string kind, IReadOnlyCollection<Guid> subjects, OrganisationId? org, CancellationToken ct, params (string Key, string Value)[] detail) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = kind,
            SubjectIds = subjects,
            ActorOrgId = org,
            Detail = detail.Length == 0 ? null : detail.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
        }, ct);

    private static Error ItemNotFound() => Error.NotFound("showcase.item_not_found", "There's nothing on the showcase like that for you.");
}
