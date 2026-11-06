using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Messaging;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Portfolio;

/// <summary>
/// A shown portfolio checked again when a family name arrives after it was shown (D80): the young
/// person linked the member that is them, or a member was added to or renamed in a household they
/// own. If the display name now carries a family name, the portfolio goes back to private and
/// they're told why; the link name is kept, so showing it again is one change of name away.
/// </summary>
/// <remarks>
/// Runs on the worker, with no person acting. Saving a shown portfolio already refuses such a name
/// (D65), so this only ever closes a page shown before the name was known. A redelivered message
/// finds the portfolio private and does nothing.
/// </remarks>
public sealed class PortfolioNameCheck(LearningDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, IEnrolmentQueries enrolments)
{
    /// <summary>The notification kind for what the platform itself tells someone (<c>ck_notification_kind</c>).</summary>
    public const string SystemKind = "system";

    public const string HiddenTitle = "notify.portfolio.hidden";

    /// <summary>Checks the account's portfolio; true when it was shown and has now been made private.</summary>
    public Task<bool> CheckAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            LearnerProfileRecord? profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct).ConfigureAwait(false);
            if (profile is null || !PortfolioVisibility.IsShown(profile.Visibility))
            {
                return false;
            }

            IReadOnlyList<string> surnames = await enrolments.SurnamesOfAsync(userId, ct).ConfigureAwait(false);
            if (!Portfolios.CarriesFamilyName(profile.DisplayName, surnames))
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
                SubjectIds = [userId],
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = "hidden_family_name" },
            }, ct).ConfigureAwait(false);
            await db.PublishAsync(tenant.TenantId, new NotificationRequested(
                userId,
                SystemKind,
                HiddenTitle,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "/portfolio/settings",
                "portfolio-hidden:" + userId.ToString("N") + ":" + now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}

/// <summary>The family names an account is known by may have changed: a portfolio it shows is checked again.</summary>
public sealed class FamilyNamesChangedHandler(PortfolioNameCheck check) : IModuleMessageHandler<FamilyNamesChanged>
{
    public async Task HandleAsync(FamilyNamesChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await check.CheckAsync(message.UserId, cancellationToken).ConfigureAwait(false);
    }
}
