using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Domain;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Identity.Persistence;

/// <summary>
/// The implementation other modules reach through
/// <see cref="IIdentityQueries"/>.
/// </summary>
/// <remarks>
/// Every method here is on an authorisation path, so they are all
/// <c>AsNoTracking</c> projections rather than entity loads: nothing should be
/// paying change-tracking cost to answer "is this person a verified guardian".
/// </remarks>
public sealed class IdentityQueries(IdentityDbContext db, IClock clock) : IIdentityQueries
{
    public async Task<YouthCapabilities> GetCapabilitiesAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        YouthCapabilityRecord? record = await db.YouthCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (record is not null)
        {
            return YouthCapabilityCalculator.FromRecord(record);
        }

        // No capability row means either an adult account or an account whose
        // row has not been written yet. Adults get the adult set; anything
        // else is denied until the calculator has run, because the safe
        // default for an unknown youth account is "nothing".
        string? accountKind = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.AccountKind)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return accountKind == AccountKind.Adult
            ? YouthCapabilities.ForAdult(userId)
            : YouthCapabilities.Denied(userId);
    }

    public async Task<IReadOnlyList<GuardianRelationship>> GetChildrenAsync(
        UserId guardianUserId,
        CancellationToken cancellationToken = default)
    {
        List<GuardianRelationship> children = await db.GuardianLinks
            .AsNoTracking()
            .Where(l => l.GuardianUserId == guardianUserId.Value
                        && l.Status == GuardianLinkStatus.Verified
                        && l.RevokedAt == null)
            .Select(l => new GuardianRelationship(
                new UserId(l.GuardianUserId),
                new UserId(l.ChildUserId),
                l.CanEnroll,
                l.CanViewActivity))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return children;
    }

    public Task<bool> IsVerifiedGuardianOfAsync(
        UserId guardianUserId,
        UserId childUserId,
        CancellationToken cancellationToken = default) =>
        db.GuardianLinks
            .AsNoTracking()
            .AnyAsync(
                l => l.GuardianUserId == guardianUserId.Value
                     && l.ChildUserId == childUserId.Value
                     && l.Status == GuardianLinkStatus.Verified
                     && l.RevokedAt == null,
                cancellationToken);

    public async Task<YouthAccessGrantSummary?> GetYouthAccessAsync(
        OrganisationId organisationId,
        UserId youthUserId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;

        // Several grants can overlap — an application, then an enrolment, then
        // a cohort membership. Take the one that runs longest, so access ends
        // when the last relationship ends rather than the first.
        YouthAccessGrant? grant = await db.YouthAccessGrants
            .AsNoTracking()
            .Where(g => g.OrgId == organisationId.Value
                        && g.YouthUserId == youthUserId.Value
                        && g.StartsAt <= now
                        && (g.EndsAt == null || g.EndsAt > now))
            .OrderByDescending(g => g.EndsAt == null)
            .ThenByDescending(g => g.EndsAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return null;
        }

        return YouthAccessBasisFromDb(grant.Basis) is { } basis
            ? new YouthAccessGrantSummary(
                youthUserId,
                organisationId,
                basis,
                grant.StartsAt,
                grant.EndsAt,
                grant.Fields)
            : null;
    }

    public Task<bool> IsContactRestrictedAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;

        // Both directions. A restriction that only blocked one way would still
        // leave the protected person visible to the restricted account.
        return db.RestrictedContacts
            .AsNoTracking()
            .AnyAsync(
                r => (r.ExpiresAt == null || r.ExpiresAt > now)
                     && ((r.ProtectedUserId == first.Value && r.RestrictedUserId == second.Value)
                         || (r.ProtectedUserId == second.Value && r.RestrictedUserId == first.Value)),
                cancellationToken);
    }

    public async Task<bool> IsMinorAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        string? kind = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.AccountKind)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // An unknown account is treated as a minor: the stricter answer is the
        // safe one for every caller that asks this question.
        return kind is null or AccountKind.Youth or AccountKind.Child;
    }

    private static YouthAccessBasis? YouthAccessBasisFromDb(string value)
    {
        foreach (YouthAccessBasis candidate in Enum.GetValues<YouthAccessBasis>())
        {
            if (string.Equals(candidate.ToDbValue(), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}
