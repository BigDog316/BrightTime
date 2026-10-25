using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Playlists;

/// <summary>
/// Learning's answer to what eligibility asks (D67): the badges a young person holds, unrevoked and
/// unexpired, and the playlists they have completed.
/// </summary>
public sealed class AchievementQueries(LearningDbContext db, IClock clock) : IAchievementQueries
{
    public Task<Achievements> OfAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            DateTimeOffset now = clock.UtcNow;
            List<Guid> badges = await db.BadgeAssertions.AsNoTracking()
                .Where(a => a.RecipientUserId == userId && a.RevokedAt == null && (a.ExpiresOn == null || a.ExpiresOn > now))
                .Select(a => a.BadgeClassId).Distinct().ToListAsync(ct).ConfigureAwait(false);
            List<Guid> playlists = await db.PlaylistEnrollments.AsNoTracking()
                .Where(e => e.UserId == userId && e.CompletedAt != null)
                .Select(e => e.PlaylistId).ToListAsync(ct).ConfigureAwait(false);
            return new Achievements(new HashSet<Guid>(badges), new HashSet<Guid>(playlists));
        }, cancellationToken);

    public Task<string?> NameAsync(string ruleKind, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => ruleKind switch
        {
            EligibilityRuleKinds.BadgeRequired => db.BadgeClasses.AsNoTracking().Where(c => c.Id == id && c.Status != Badges.Badges.Draft)
                .Select(c => c.Name).FirstOrDefaultAsync(ct),
            EligibilityRuleKinds.PlaylistCompleted => db.Playlists.AsNoTracking().Where(p => p.Id == id && p.Status != Playlists.Draft)
                .Select(p => p.Title).FirstOrDefaultAsync(ct),
            _ => Task.FromResult<string?>(null),
        }, cancellationToken);
}
