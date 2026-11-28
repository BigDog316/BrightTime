using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Modules.Identity.Contracts;

/// <summary>A verified guardian-of relationship.</summary>
/// <remarks>
/// <see cref="IsChildProfile"/> is true while the account is still a child profile (under 13,
/// managed by the guardian), and false once its owner has claimed it as their own youth account (D35).
/// </remarks>
[YouthData]
public sealed record GuardianRelationship(UserId GuardianUserId, UserId ChildUserId, bool CanEnroll, bool CanViewActivity)
{
    public bool IsChildProfile { get; init; }
}

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
/// Whose consent decides a scope for a young person (D124): a guardian's, for a child account or a
/// teenager a verified guardian manages, and then the live consent's id, if there is one; or their
/// own, for a teenager without a guardian link. As the capability calculator decides it.
/// </summary>
public sealed record ConsentBasis(bool GuardianManaged, Guid? ConsentId);

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

    /// <summary>Whose consent decides <paramref name="scope"/> for this young person, and the guardian's live consent, if any (D124).</summary>
    Task<ConsentBasis> GetConsentBasisAsync(UserId youthUserId, ConsentScope scope, CancellationToken cancellationToken = default);

    /// <summary>The account's roles (<c>youth</c>, <c>network_organiser</c> and so on), to say what someone was acting as.</summary>
    Task<IReadOnlyList<string>> GetRoleNamesAsync(UserId userId, CancellationToken cancellationToken = default);

    /// <summary>The addresses of these adult accounts, for a notice by email (D98). Young people and children are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetAdultEmailsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tenant's live accounts whose roles grant <paramref name="permission"/>, such as the
    /// moderators to tell of a critical report (D115). By role, as the sign-in claims are built.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersWithPermissionAsync(string permission, CancellationToken cancellationToken = default);
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
