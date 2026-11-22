using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Portfolio;

/// <summary>
/// A young person's learning records by the ids Learning audits them with (D96): what they took
/// part in, their goals, skills and projects, the badges they were awarded and the playlists they
/// started. The portfolio itself is audited by their own id.
/// </summary>
public sealed class LearningAuditSubjects(LearningDbContext db) : IAuditSubjects
{
    public Task<IReadOnlyCollection<Guid>> RecordsOfAsync(UserId person, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyCollection<Guid>>(async ct =>
        {
            Guid id = person.Value;
            List<Guid> ids = [];
            ids.AddRange(await db.Participation.AsNoTracking().Where(r => r.UserId == id).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.Goals.AsNoTracking().Where(r => r.UserId == id).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.Skills.AsNoTracking().Where(r => r.UserId == id).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.Projects.AsNoTracking().Where(r => r.UserId == id).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.BadgeAssertions.AsNoTracking().Where(r => r.RecipientUserId == id).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.PlaylistEnrollments.AsNoTracking().Where(r => r.UserId == id).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false));
            return ids;
        }, cancellationToken);
}
