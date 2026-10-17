using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Modules.Identity.Contracts;

/// <summary>A verified guardian-of relationship.</summary>
[YouthData]
public sealed record GuardianRelationship(UserId GuardianUserId, UserId ChildUserId, bool CanEnroll, bool CanViewActivity);

/// <summary>A live grant of access to one young person's record.</summary>
[YouthData]
public sealed record YouthAccessGrantSummary(
    UserId YouthUserId,
    OrganisationId OrganisationId,
    YouthAccessBasis Basis,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<string> Fields);

/// <summary>
/// What other modules may ask Identity. Deliberately narrow: this is the whole
/// surface, and every method on it is a question the authorisation layer needs
/// answered rather than a way to read user rows.
/// </summary>
public interface IIdentityQueries
{
    Task<YouthCapabilities> GetCapabilitiesAsync(UserId userId, CancellationToken cancellationToken = default);

    /// <summary>Verified, unrevoked guardian links for a guardian.</summary>
    Task<IReadOnlyList<GuardianRelationship>> GetChildrenAsync(UserId guardianUserId, CancellationToken cancellationToken = default);

    Task<bool> IsVerifiedGuardianOfAsync(UserId guardianUserId, UserId childUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether an organisation currently has a reason to see this young
    /// person's record. Returns the grant so the caller can audit the basis.
    /// </summary>
    Task<YouthAccessGrantSummary?> GetYouthAccessAsync(
        OrganisationId organisationId,
        UserId youthUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a contact restriction exists between two accounts, in either
    /// direction. Custody orders and safety plans live here.
    /// </summary>
    Task<bool> IsContactRestrictedAsync(UserId first, UserId second, CancellationToken cancellationToken = default);

    Task<bool> IsMinorAsync(UserId userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writing a youth-access grant. Called by Enrollment, Community and Workforce
/// when a relationship starts or ends.
/// </summary>
public interface IYouthAccessWriter
{
    /// <summary>
    /// Opens or extends a grant. Idempotent on (organisation, youth, basis,
    /// basisRefId) so replaying an enrolment event does not stack grants.
    /// </summary>
    Task GrantAsync(
        OrganisationId organisationId,
        UserId youthUserId,
        YouthAccessBasis basis,
        Guid? basisRefId,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a grant at the end of its retention grace window rather than
    /// immediately: a provider still needs to run last season's reports.
    /// </summary>
    Task EndAsync(
        OrganisationId organisationId,
        UserId youthUserId,
        YouthAccessBasis basis,
        Guid? basisRefId,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken = default);
}
