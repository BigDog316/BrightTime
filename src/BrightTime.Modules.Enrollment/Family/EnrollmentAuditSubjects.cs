using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Family;

/// <summary>
/// A person's enrolment records by the ids Enrollment and Attendance audit them with (D96): the
/// household profiles they own or are a member of, the applications made for them, and their
/// enrolments (which attendance is recorded against).
/// </summary>
/// <remarks>
/// A household profile names everyone in it, so a read of it is a read about each of them. Fee
/// payments are the payer's, not the young person's, and aren't listed.
/// </remarks>
public sealed class EnrollmentAuditSubjects(EnrollmentDbContext db) : IAuditSubjects
{
    public Task<IReadOnlyCollection<Guid>> RecordsOfAsync(UserId person, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyCollection<Guid>>(async ct =>
        {
            Guid id = person.Value;
            List<Guid> members = await db.FamilyMembers.AsNoTracking().Where(m => m.UserId == id).Select(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> ids = [];
            ids.AddRange(await db.FamilyProfiles.AsNoTracking()
                .Where(p => p.OwnerUserId == id || db.FamilyMembers.Any(m => m.FamilyProfileId == p.Id && m.UserId == id))
                .Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.Applications.AsNoTracking().Where(a => members.Contains(a.FamilyMemberId)).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false));
            ids.AddRange(await db.Enrollments.AsNoTracking().Where(e => e.YouthUserId == id || members.Contains(e.FamilyMemberId)).Select(e => e.Id).ToListAsync(ct).ConfigureAwait(false));
            return ids;
        }, cancellationToken);
}
