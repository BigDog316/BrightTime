using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Modules.Attendance.Contracts;

/// <summary>
/// An enrolment's attendance changed: a record made, synced, checked in or corrected (D65). Carries
/// only the enrolment; whoever cares reads what they need through <see cref="IAttendanceQueries"/>.
/// </summary>
public sealed record AttendanceChanged(Guid EnrollmentId) : IModuleMessage
{
    public static string MessageType => "attendance.changed";
}

/// <summary>One meeting's current record for an enrolment: no notes, no device, nothing but what was recorded.</summary>
public sealed record AttendanceFact(DateTimeOffset OccurrenceStartsAt, string State, short? MinutesAttended);

/// <summary>Attendance for the modules that build on it (the learner's participation first).</summary>
public interface IAttendanceQueries
{
    /// <summary>The enrolment's current records (corrections applied), earliest first.</summary>
    Task<IReadOnlyList<AttendanceFact>> ForEnrollmentAsync(Guid enrollmentId, CancellationToken cancellationToken = default);
}
