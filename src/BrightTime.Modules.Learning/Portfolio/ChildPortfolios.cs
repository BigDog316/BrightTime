using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Badges;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Records;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Portfolio;

/// <summary>
/// A child's portfolio as their guardian sees it (D93): what the child took part in, the badges
/// they hold and the skills organisations endorsed. There's nothing in it for the guardian to change.
/// </summary>
[YouthData]
public sealed record ChildPortfolioView(
    decimal TotalHours, IReadOnlyList<ParticipationView> Participation, IReadOnlyList<HeldBadge> Badges, IReadOnlyList<SkillView> Skills);

/// <summary>
/// A guardian's view and download of their child's portfolio (D93), as docs/plan/04 gives them
/// (<c>portfolio.view_own</c> and <c>portfolio.export</c>, for their children).
/// </summary>
/// <remarks>
/// <para>
/// Only while the account is a child profile: under 13, with no sign-in of its own. Once its owner
/// claims it (D35), the portfolio is theirs, as a youth account's always is, and the guardian no
/// longer sees it. The link must be verified, not revoked, and allow seeing the child's activity
/// (<c>can_view_activity</c>).
/// </para>
/// <para>
/// The view is read as the guardian, so the audit trail records who read the child's records.
/// A download is the child's own export (D68), recorded the same way.
/// </para>
/// </remarks>
public sealed class ChildPortfolios(
    LearningDbContext db, AuditTrail audit, IIdentityQueries identity, Badges.Badges badges, Skills skills, PortfolioExports exports)
{
    public const string Read = "learning.portfolio.guardian_read";

    public Task<Result<ChildPortfolioView>> GetAsync(UserId guardian, UserId child, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            if (await RefusedAsync(guardian, child, ct).ConfigureAwait(false) is { } refused)
            {
                return Result<ChildPortfolioView>.Failure(refused);
            }

            List<ParticipationRecordRecord> records = await db.Participation.AsNoTracking().Where(p => p.UserId == child.Value)
                .OrderByDescending(p => p.StartedOn).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<HeldBadge> held = await badges.HeldAsync(child, site, ct).ConfigureAwait(false);
            IReadOnlyList<SkillView> listed = await skills.OfAsync(child.Value, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "learner_profile", SubjectIds = [child.Value] }, ct).ConfigureAwait(false);
            return Result<ChildPortfolioView>.Success(new ChildPortfolioView(
                records.Where(r => !r.IsHidden).Sum(r => r.Hours), [.. records.Select(Portfolios.View)], held, listed));
        }, cancellationToken);

    /// <summary>The facts for the child's printable portfolio. The export is recorded.</summary>
    public Task<Result<PortfolioFacts>> FactsAsync(UserId guardian, UserId child, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await RefusedAsync(guardian, child, ct).ConfigureAwait(false) is { } refused
                ? Result<PortfolioFacts>.Failure(refused)
                : Result<PortfolioFacts>.Success(await exports.FactsAsync(child, site, ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>The child's badges as OB 3.0 credentials, or a CLR 2.0 holding them.</summary>
    public Task<Result<ExportFile>> CredentialsAsync(UserId guardian, UserId child, Uri site, string siteName, string format, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await RefusedAsync(guardian, child, ct).ConfigureAwait(false) is { } refused
                ? Result<ExportFile>.Failure(refused)
                : await exports.CredentialsAsync(child, site, siteName, format, ct).ConfigureAwait(false),
            cancellationToken);

    /// <summary>Why this guardian may not see this portfolio; null when they may.</summary>
    private async Task<Error?> RefusedAsync(UserId guardian, UserId child, CancellationToken ct)
    {
        GuardianRelationship? link = (await identity.GetChildrenAsync(guardian, ct).ConfigureAwait(false)).FirstOrDefault(c => c.ChildUserId == child);
        if (link is null)
        {
            return Error.NotFound("portfolio.child_not_found", "You aren't the verified guardian of a child profile like that.");
        }

        if (!link.CanViewActivity)
        {
            return Error.Forbidden("portfolio.guardian_view", "Your link to this child doesn't include seeing what they take part in.");
        }

        return link.IsChildProfile
            ? null
            : Error.Forbidden("portfolio.child_claimed", "Their portfolio is theirs now that they have their own account.");
    }
}
