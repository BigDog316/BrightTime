using BrightTime.SharedKernel.Primitives;

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

/// <summary>
/// The family behind an enrolment or a submitted application, as messaging needs it: the account
/// that owns the family profile, and the organisation and listing it concerns (D53).
/// </summary>
public sealed record FamilyContact(Guid OwnerUserId, Guid OrgId, Guid ListingId);

/// <summary>
/// What a report counts about an enrolment (D57): when it began and ended, and the young person's
/// age band on a day, from the month of birth. No name, no date of birth. Reports use the age band
/// only inside suppressed aggregates.
/// </summary>
public sealed record EnrolmentFacts(Guid EnrollmentId, Guid FamilyMemberId, Guid ListingId, Guid OrgId, string Status, DateTimeOffset EnrolledAt, DateTimeOffset? EndedAt, AgeBand AgeBand);

/// <summary>Enrolments, for the modules that hang records off them (attendance first).</summary>
public interface IEnrolmentQueries
{
    Task<IReadOnlyList<EnrolmentSummary>> GetAsync(IReadOnlyCollection<Guid> enrollmentIds, CancellationToken cancellationToken = default);

    /// <summary>A listing's active, enrolled (not waitlisted) young people.</summary>
    Task<IReadOnlyList<RosterEntry>> RosterAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>The enrolments of everyone on <paramref name="ownerUserId"/>'s family profile.</summary>
    Task<IReadOnlyList<FamilyEnrolment>> ForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>The family behind an enrolment, or behind a submitted (never a draft) application; null when there is none.</summary>
    Task<FamilyContact?> FamilyOfAsync(Guid? enrollmentId, Guid? applicationId, CancellationToken cancellationToken = default);

    /// <summary>The accounts owning the family profiles of a listing's active, enrolled young people (D54).</summary>
    Task<IReadOnlyList<Guid>> FamilyOwnersAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>What reports count about these enrolments, with each young person's age band on <paramref name="asOf"/> (D57).</summary>
    Task<IReadOnlyList<EnrolmentFacts>> FactsAsync(IReadOnlyCollection<Guid> enrollmentIds, DateOnly asOf, CancellationToken cancellationToken = default);
}
