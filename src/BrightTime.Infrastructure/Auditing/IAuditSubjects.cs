using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Auditing;

/// <summary>
/// The records a module keeps about one person, by the ids its audit rows name them with (D96).
/// </summary>
/// <remarks>
/// An audit row's <c>subject_ids</c> holds whatever the module audited: sometimes the person's
/// own id, often a record's (an application, an enrolment, a project, a badge). The transparency
/// screen asks every module that keeps records about people which of its ids are this person's,
/// and reads the trail for any of them. A module that names people only by their own id needs no
/// implementation.
/// </remarks>
public interface IAuditSubjects
{
    Task<IReadOnlyCollection<Guid>> RecordsOfAsync(UserId person, CancellationToken cancellationToken = default);
}
