using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Directory.Vetting;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Review;

public sealed record OrganisationQueueRow(
    OrganisationId Id,
    string DisplayName,
    string Kind,
    OrganisationStatus Status,
    DateTimeOffset? SubmittedAt);

public sealed record VettingHistoryRow(string? From, string To, string? Note, DateTimeOffset OccurredAt);

public sealed record OrganisationForReview(
    OrganisationId Id,
    string DisplayName,
    string LegalName,
    string Kind,
    OrganisationStatus Status,
    string? WebsiteUrl,
    string? Mission,
    string? PublicEmail,
    string? PublicPhone,
    IReadOnlyList<string> LanguagesServed,
    DateTimeOffset? SubmittedAt,
    string? ReviewNote,
    IReadOnlyList<string> Sites,
    int ActiveOwners,
    IReadOnlyList<VettingHistoryRow> History);

/// <summary>
/// The organiser's side of vetting: the queue, one application, and decisions on it.
/// </summary>
/// <remarks>
/// Every decision goes through <see cref="OrganisationVetting"/> and writes an append-only
/// <c>organisation_vetting_event</c> in the same transaction as the status change, so the
/// history and the state cannot disagree. The caller authorises the organiser.
/// </remarks>
public sealed class OrganisationReview(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    private static readonly string[] Waiting =
        [OrganisationStatus.Applied.ToDbValue(), OrganisationStatus.UnderReview.ToDbValue()];

    /// <summary>Applications awaiting an organiser, oldest first.</summary>
    public Task<IReadOnlyList<OrganisationQueueRow>> QueueAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<OrganisationQueueRow>>(async ct =>
        {
            List<Organisation> rows = await db.Organisations.AsNoTracking()
                .Where(o => Waiting.Contains(o.Status))
                .OrderBy(o => o.SubmittedAt).ThenBy(o => o.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. rows.Select(o => new OrganisationQueueRow(o.OrganisationId, o.DisplayName, o.OrgKind, o.ResolvedStatus, o.SubmittedAt))];
        }, cancellationToken);

    public Task<OrganisationForReview?> GetAsync(OrganisationId id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Organisation? o = await db.Organisations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value, ct).ConfigureAwait(false);
            if (o is null)
            {
                return null;
            }

            List<string> sites = await db.Sites.AsNoTracking()
                .Where(s => s.OrgId == o.Id && s.IsActive)
                .OrderBy(s => s.Name)
                .Select(s => s.Locality == null ? s.Name : s.Name + ", " + s.Locality)
                .ToListAsync(ct).ConfigureAwait(false);

            int owners = await CountOwnersAsync(o.Id, ct).ConfigureAwait(false);

            List<VettingHistoryRow> history = await db.VettingEvents.AsNoTracking()
                .Where(e => e.OrgId == o.Id)
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => new VettingHistoryRow(e.FromStatus, e.ToStatus, e.Note, e.OccurredAt))
                .ToListAsync(ct).ConfigureAwait(false);

            return new OrganisationForReview(
                o.OrganisationId, o.DisplayName, o.LegalName, o.OrgKind, o.ResolvedStatus, o.WebsiteUrl, o.Mission,
                o.PublicEmail, o.PublicPhone, o.LanguagesServed, o.SubmittedAt, o.ReviewNote, sites, owners, history);
        }, cancellationToken);

    /// <summary>An organiser's decision. Rejection, change requests and suspension need a reason.</summary>
    public Task<Result> DecideAsync(
        OrganisationId id,
        OrganisationStatus to,
        string? reason,
        UserId actor,
        CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Organisation? o = await db.Organisations.FirstOrDefaultAsync(x => x.Id == id.Value, ct).ConfigureAwait(false);
            if (o is null)
            {
                return Result.Failure(Error.NotFound("organisation.not_found", "That organisation does not exist."));
            }

            OrganisationStatus from = o.ResolvedStatus;
            bool hasSite = await db.Sites.AnyAsync(s => s.OrgId == o.Id && s.IsActive, ct).ConfigureAwait(false);
            int owners = await CountOwnersAsync(o.Id, ct).ConfigureAwait(false);

            // Insurance and clearance checks need tenant settings and clearance data that do
            // not exist yet; until they do, the context says "not required" and "cleared".
            Result allowed = OrganisationVetting.CanTransition(from, to, OrganisationVetting.Mover.Organiser,
                new OrganisationVetting.TransitionContext
                {
                    Reason = reason,
                    HasSite = hasSite,
                    HasActiveOwner = owners > 0,
                });
            if (!allowed.IsSuccess)
            {
                return allowed;
            }

            DateTimeOffset now = clock.UtcNow;
            o.Status = to.ToDbValue();
            o.ReviewedAt = now;
            o.ReviewedBy = actor.Value;
            o.ReviewNote = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

            // ck_organisation_approved: approved_at exists exactly while approved.
            o.ApprovedAt = to == OrganisationStatus.Approved ? now : null;
            if (to == OrganisationStatus.Suspended)
            {
                o.SuspendedAt = now;
                o.SuspensionReason = reason!.Trim();
            }
            else
            {
                o.SuspendedAt = null;
                o.SuspensionReason = null;
            }

            db.VettingEvents.Add(new OrganisationVettingEvent
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = o.Id,
                FromStatus = from.ToDbValue(),
                ToStatus = to.ToDbValue(),
                ActorId = actor.Value,
                Note = o.ReviewNote,
                OccurredAt = now,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "organisation.status_changed",
                ActorId = actor,
                SubjectKind = "organisation",
                SubjectIds = [o.Id],
                Reason = o.ReviewNote,
                Detail = new Dictionary<string, string> { ["from"] = from.ToDbValue(), ["to"] = to.ToDbValue() },
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    private Task<int> CountOwnersAsync(Guid orgId, CancellationToken ct) =>
        db.Memberships.CountAsync(
            m => m.OrgId == orgId && m.OrgRole == "owner" && m.Status == MembershipStatus.Active && m.EndedAt == null, ct);
}
