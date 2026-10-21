namespace BrightTime.Modules.Enrollment.Contracts;

/// <summary>An enrolment as other modules see it: whose, where, and whether it is live.</summary>
public sealed record EnrolmentSummary(Guid Id, Guid ListingId, Guid OrgId, Guid FamilyMemberId, string SeatKind, string Status, DateTimeOffset? EndedAt);

/// <summary>
/// One line of a roster: the enrolment, and the young person's display name (first name and last
/// initial) when the family's live share names them; null when it doesn't (D44, D49).
/// </summary>
public sealed record RosterEntry(Guid EnrollmentId, string? DisplayName);

/// <summary>An enrolment of someone on the asking account's family profile.</summary>
public sealed record FamilyEnrolment(Guid EnrollmentId, Guid MemberId, string MemberFirstName, Guid ListingId, string Status);

/// <summary>Enrolments, for the modules that hang records off them (attendance first).</summary>
public interface IEnrolmentQueries
{
    Task<IReadOnlyList<EnrolmentSummary>> GetAsync(IReadOnlyCollection<Guid> enrollmentIds, CancellationToken cancellationToken = default);

    /// <summary>A listing's active, enrolled (not waitlisted) young people.</summary>
    Task<IReadOnlyList<RosterEntry>> RosterAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>The enrolments of everyone on <paramref name="ownerUserId"/>'s family profile.</summary>
    Task<IReadOnlyList<FamilyEnrolment>> ForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
