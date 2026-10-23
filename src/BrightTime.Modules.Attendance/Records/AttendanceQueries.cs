using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Attendance.Records;

/// <summary>
/// Attendance for other modules (D65): an enrolment's current records, and nothing that says who
/// recorded them, from which device, or what the note said.
/// </summary>
public sealed class AttendanceQueries(AttendanceDbContext db) : IAttendanceQueries
{
    public Task<IReadOnlyList<AttendanceFact>> ForEnrollmentAsync(Guid enrollmentId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<AttendanceFact>>(async ct =>
            [.. await db.Records.AsNoTracking()
                .Where(r => r.EnrollmentId == enrollmentId && r.SupersededBy == null)
                .OrderBy(r => r.OccurrenceStartsAt)
                .Select(r => new AttendanceFact(r.OccurrenceStartsAt, r.State, r.MinutesAttended))
                .ToListAsync(ct).ConfigureAwait(false)], cancellationToken);
}
