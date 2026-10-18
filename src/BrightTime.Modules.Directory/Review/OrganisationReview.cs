using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Directory.Provider;
using BrightTime.Modules.Directory.Vetting;
using BrightTime.Modules.Platform.Contracts;
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
    IReadOnlyList<VettingHistoryRow> History,
    VettingState Vetting);

/// <summary>
/// The organiser's side of vetting: the queue, one application, and decisions on it.
/// </summary>
/// <remarks>
/// Every decision goes through <see cref="OrganisationVetting"/> and writes an append-only
/// <c>organisation_vetting_event</c> in the same transaction as the status change, so the
/// history and the state cannot disagree. The caller authorises the organiser.
/// </remarks>
public sealed class OrganisationReview(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit, IFeatureFlags flags)
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

            VettingState vetting = await VettingStateAsync(o, sites.Count > 0, owners, ct).ConfigureAwait(false);
            return new OrganisationForReview(
                o.OrganisationId, o.DisplayName, o.LegalName, o.OrgKind, o.ResolvedStatus, o.WebsiteUrl, o.Mission,
                o.PublicEmail, o.PublicPhone, o.LanguagesServed, o.SubmittedAt, o.ReviewNote, sites, owners, history, vetting);
        }, cancellationToken);

    /// <summary>
    /// Saves the checklist without deciding: vetting takes days, and what was checked must
    /// survive between visits. Recorded as a vetting event that does not change the status.
    /// </summary>
    public Task<Result> SaveChecklistAsync(OrganisationId id, VettingChecklist checklist, UserId actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        return db.InTransactionAsync(async ct =>
        {
            Organisation? o = await db.Organisations.FirstOrDefaultAsync(x => x.Id == id.Value, ct).ConfigureAwait(false);
            if (o is null)
            {
                return Result.Failure(Error.NotFound("organisation.not_found", "That organisation does not exist."));
            }

            if (o.ResolvedStatus is OrganisationStatus.Rejected or OrganisationStatus.Withdrawn or OrganisationStatus.Draft)
            {
                return Result.Failure(Error.Conflict("organisation.terminal_state",
                    "An organisation that is not being vetted has no checklist to save.",
                    new Dictionary<string, object> { ["status"] = OrganisationVetting.StatusPhrase(o.ResolvedStatus) }));
            }

            DateTimeOffset now = clock.UtcNow;
            o.InsuranceExpiresOn = checklist.InsuranceExpiresOn;
            o.UpdatedAt = now;
            db.VettingEvents.Add(new OrganisationVettingEvent
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = o.Id,
                FromStatus = o.Status,
                ToStatus = o.Status,
                ActorId = actor.Value,
                Checklist = checklist.ToJson(),
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "organisation.checklist_saved",
                ActorId = actor,
                SubjectKind = "organisation",
                SubjectIds = [o.Id],
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);
    }

    private async Task<VettingState> VettingStateAsync(Organisation o, bool hasSite, int owners, CancellationToken ct)
    {
        string? saved = null;
        DateTimeOffset? savedAt = null;
        foreach (var e in await db.VettingEvents.AsNoTracking()
                     .Where(e => e.OrgId == o.Id)
                     .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
                     .Select(e => new { e.Checklist, e.OccurredAt })
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            if (VettingChecklist.FromJson(e.Checklist) is not null)
            {
                saved = e.Checklist;
                savedAt = e.OccurredAt;
                break;
            }
        }

        DateOnly today = clock.TodayIn(tenant.TimeZone);
        string[] youthFacing = [MemberRoles.Staff, MemberRoles.Mentor];
        List<Guid> staff = await db.Memberships.AsNoTracking()
            .Where(m => m.OrgId == o.Id && m.Status == MembershipStatus.Active && youthFacing.Contains(m.OrgRole))
            .Select(m => m.UserId)
            .ToListAsync(ct).ConfigureAwait(false);
        string background = ClearanceKind.BackgroundCheck.ToDbValue();
        string[] passing = [ClearanceStatus.Cleared.ToDbValue(), ClearanceStatus.Conditional.ToDbValue()];
        int checkedStaff = await db.Clearances.AsNoTracking()
            .Where(c => staff.Contains(c.UserId) && (c.OrgId == null || c.OrgId == o.Id)
                        && c.ClearanceKind == background && passing.Contains(c.Status)
                        && (c.ExpiresOn == null || c.ExpiresOn >= today))
            .Select(c => c.UserId).Distinct()
            .CountAsync(ct).ConfigureAwait(false);

        return new VettingState(
            (saved is null ? null : VettingChecklist.FromJson(saved)) ?? new VettingChecklist(),
            savedAt,
            hasSite,
            owners,
            await flags.IsEnabledAsync(FeatureFlagKeys.VettingInsuranceRequired, ct).ConfigureAwait(false),
            o.InsuranceExpiresOn,
            o.InsuranceExpiresOn is { } expires && expires >= today,
            staff.Count,
            staff.Count - checkedStaff);
    }

    /// <summary>An organiser's decision. Rejection, change requests and suspension need a reason.</summary>
    public Task<Result> DecideAsync(
        OrganisationId id,
        OrganisationStatus to,
        string? reason,
        UserId actor,
        CancellationToken cancellationToken = default) =>
        DecideAsync(id, to, reason, null, actor, cancellationToken);

    /// <summary>
    /// An organiser's decision, with the checklist as ticked on the same form. Approval needs
    /// the contact verified, and a current certificate of insurance where the tenant requires
    /// one; the checklist given, or else the last one saved, is what is judged and recorded.
    /// </summary>
    public Task<Result> DecideAsync(
        OrganisationId id,
        OrganisationStatus to,
        string? reason,
        VettingChecklist? checklist,
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
            if (checklist is not null)
            {
                o.InsuranceExpiresOn = checklist.InsuranceExpiresOn;
            }

            VettingState vetting = await VettingStateAsync(o, hasSite, owners, ct).ConfigureAwait(false);
            VettingChecklist judged = checklist ?? vetting.Checklist;

            // Insurance and clearance checks need tenant settings and clearance data that do
            // not exist yet; until they do, the context says "not required" and "cleared".
            Result allowed = OrganisationVetting.CanTransition(from, to, OrganisationVetting.Mover.Organiser,
                new OrganisationVetting.TransitionContext
                {
                    Reason = reason,
                    HasSite = hasSite,
                    HasActiveOwner = owners > 0,
                    ContactVerified = judged.ContactVerified,
                    TenantRequiresInsurance = vetting.InsuranceRequired,
                    HasCurrentInsurance = o.InsuranceExpiresOn is { } expires && expires >= clock.TodayIn(tenant.TimeZone),
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
                Checklist = checklist?.ToJson() ?? "{}",
                OccurredAt = now,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.QueueOrganisationChangedAsync(tenant.TenantId, o.Id, ct).ConfigureAwait(false);
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
