using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Messaging;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Portfolio;

/// <summary>
/// Learning's answers for reporting a shown portfolio (D128): the page as anyone sees it, by its
/// link name or its owner, with its shown projects. The caller audits what it reads.
/// </summary>
public sealed class PortfolioModeration(LearningDbContext db) : IPortfolioModeration
{
    public Task<ReportablePortfolio?> ShownAsync(string slug, CancellationToken cancellationToken = default)
    {
        string name = (slug ?? string.Empty).Trim().ToLowerInvariant();
        return name.Length == 0
            ? Task.FromResult<ReportablePortfolio?>(null)
            : db.InTransactionAsync(async ct =>
                await db.Profiles.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.PublicSlug == name && (p.Visibility == PortfolioVisibility.LinkOnly || p.Visibility == PortfolioVisibility.Public), ct)
                    .ConfigureAwait(false) is { } profile
                    ? await ViewAsync(profile, ct).ConfigureAwait(false)
                    : null, cancellationToken);
    }

    public Task<ReportablePortfolio?> OfAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct).ConfigureAwait(false) is { } profile
                ? await ViewAsync(profile, ct).ConfigureAwait(false)
                : null, cancellationToken);

    private async Task<ReportablePortfolio?> ViewAsync(LearnerProfileRecord profile, CancellationToken ct)
    {
        List<ReportableProject> projects = [.. (await db.Projects.AsNoTracking()
                .Where(p => p.UserId == profile.UserId && p.Status == "published" && (p.Visibility == "public" || p.Visibility == "link_only"))
                .OrderByDescending(p => p.ApprovedAt).Select(p => new { p.Title, p.Description }).ToListAsync(ct).ConfigureAwait(false))
            .Select(p => new ReportableProject(p.Title, p.Description))];
        return new ReportablePortfolio(profile.UserId, profile.PublicSlug ?? "", profile.DisplayName, profile.Headline, profile.Bio, profile.Visibility,
            projects, profile.UpdatedAt);
    }
}

/// <summary>
/// A moderator took a reported portfolio down (D128): it goes back to private, keeping its link
/// name, and its owner is told. They can change it and show it again; what they write is screened
/// again (D118). Runs on the worker; a redelivered message finds it private and does nothing.
/// </summary>
public sealed class PortfolioTakenDownHandler(LearningDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock) : IModuleMessageHandler<PortfolioTakenDown>
{
    public const string TakenDownTitle = "notify.portfolio.taken_down";

    public Task HandleAsync(PortfolioTakenDown message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return db.InTransactionAsync(async ct =>
        {
            LearnerProfileRecord? profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == message.UserId, ct).ConfigureAwait(false);
            if (profile is null || !PortfolioVisibility.IsShown(profile.Visibility))
            {
                return false;
            }

            DateTimeOffset now = clock.StoredNow();
            profile.Visibility = PortfolioVisibility.Private;
            profile.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Portfolios.Changed,
                SubjectKind = "learner_profile",
                SubjectIds = [message.UserId],
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = "taken_down", ["report"] = message.ReportId.ToString("D") },
            }, ct).ConfigureAwait(false);
            await db.PublishAsync(tenant.TenantId, new NotificationRequested(
                message.UserId,
                PortfolioNameCheck.SystemKind,
                TakenDownTitle,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "/portfolio/settings",
                "portfolio-taken-down:" + message.ReportId.ToString("N")), ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }
}
