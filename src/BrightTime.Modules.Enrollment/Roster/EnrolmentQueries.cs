using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Roster;

/// <summary>
/// Enrolments for other modules (D49). Names leave this module only as a roster's display name,
/// and only where the family's live share with the organisation names the young person.
/// </summary>
public sealed class EnrolmentQueries(EnrollmentDbContext db, IFieldCipher cipher, ITenantContext tenant, IClock clock, IListingDetails listings) : IEnrolmentQueries
{
    public Task<FamilyContact?> FamilyOfAsync(Guid? enrollmentId, Guid? applicationId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<FamilyContact?>(async ct =>
        {
            if (enrollmentId is { } e)
            {
                var found = await (from en in db.Enrollments.AsNoTracking()
                                   join m in db.FamilyMembers on en.FamilyMemberId equals m.Id
                                   join p in db.FamilyProfiles on m.FamilyProfileId equals p.Id
                                   where en.Id == e
                                   select new { p.OwnerUserId, en.OrgId, en.ListingId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return found is null ? null : new FamilyContact(found.OwnerUserId, found.OrgId, found.ListingId);
            }

            if (applicationId is { } a)
            {
                var found = await (from ap in db.Applications.AsNoTracking()
                                   join p in db.FamilyProfiles on ap.FamilyProfileId equals p.Id
                                   where ap.Id == a && ap.Status != ApplicationStatuses.Draft
                                   select new { p.OwnerUserId, ap.ListingId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (found is null || (await listings.GetForReviewAsync(tenant.TenantId, found.ListingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId is not { } org)
                {
                    return null;
                }

                return new FamilyContact(found.OwnerUserId, org.Value, found.ListingId);
            }

            return null;
        }, cancellationToken);

    public Task<IReadOnlyList<Guid>> FamilyOwnersAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<Guid>>(async ct =>
            [.. await (from en in db.Enrollments.AsNoTracking()
                       join m in db.FamilyMembers on en.FamilyMemberId equals m.Id
                       join p in db.FamilyProfiles on m.FamilyProfileId equals p.Id
                       where en.ListingId == listingId && en.Status == EnrollmentStatuses.Active && en.SeatKind == EnrollmentKinds.Enrolled
                       select p.OwnerUserId).Distinct().ToListAsync(ct).ConfigureAwait(false)], cancellationToken);

    public Task<IReadOnlyList<EnrolmentFacts>> FactsAsync(IReadOnlyCollection<Guid> enrollmentIds, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrollmentIds);
        Guid[] ids = [.. enrollmentIds.Distinct()];
        return db.InTransactionAsync<IReadOnlyList<EnrolmentFacts>>(async ct =>
        {
            var found = await (from e in db.Enrollments.AsNoTracking()
                               join m in db.FamilyMembers on e.FamilyMemberId equals m.Id
                               where ids.Contains(e.Id)
                               select new { e.Id, e.FamilyMemberId, e.ListingId, e.OrgId, e.Status, e.EnrolledAt, e.EndedAt, m.BirthMonth, e.YouthUserId }).ToListAsync(ct).ConfigureAwait(false);
            return [.. found.Select(f => new EnrolmentFacts(f.Id, f.FamilyMemberId, f.ListingId, f.OrgId, f.Status, f.EnrolledAt, f.EndedAt,
                f.BirthMonth is { } month ? Age.BandFromBirthMonth(month, asOf) : AgeBand.Unknown, f.YouthUserId))];
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SurnamesOfAsync(Guid youthUserId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<string>>(async ct =>
        {
            List<byte[]> sealedNames = await db.FamilyMembers.AsNoTracking().Where(m => m.UserId == youthUserId).Select(m => m.LastNameEnc).ToListAsync(ct).ConfigureAwait(false);
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (byte[] name in sealedNames)
            {
                if (await cipher.DecryptAsync(tenant.TenantId, name, ct).ConfigureAwait(false) is { Length: > 0 } plain)
                {
                    names.Add(plain.Trim());
                }
            }

            return [.. names];
        }, cancellationToken);

    public Task<IReadOnlyList<EnrolmentSummary>> GetAsync(IReadOnlyCollection<Guid> enrollmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrollmentIds);
        Guid[] ids = [.. enrollmentIds.Distinct()];
        return db.InTransactionAsync<IReadOnlyList<EnrolmentSummary>>(async ct =>
            [.. await db.Enrollments.AsNoTracking().Where(e => ids.Contains(e.Id))
                .Select(e => new EnrolmentSummary(e.Id, e.ListingId, e.OrgId, e.FamilyMemberId, e.SeatKind, e.Status, e.EndedAt))
                .ToListAsync(ct).ConfigureAwait(false)], cancellationToken);
    }

    public Task<IReadOnlyList<RosterEntry>> RosterAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<RosterEntry>>(async ct =>
        {
            List<EnrollmentRecord> enrolled = await db.Enrollments.AsNoTracking()
                .Where(e => e.ListingId == listingId && e.Status == EnrollmentStatuses.Active && e.SeatKind == EnrollmentKinds.Enrolled)
                .OrderBy(e => e.EnrolledAt).ToListAsync(ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            List<RosterEntry> roster = [];
            foreach (EnrollmentRecord e in enrolled)
            {
                FamilyMemberRecord member = await db.FamilyMembers.AsNoTracking().FirstAsync(m => m.Id == e.FamilyMemberId, ct).ConfigureAwait(false);
                Guid memberId = member.Id;
                bool named = await db.ProfileShares.AsNoTracking().AnyAsync(s =>
                    s.FamilyProfileId == member.FamilyProfileId && s.OrgId == e.OrgId && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now)
                    && (s.ListingId == null || s.ListingId == listingId) && s.MemberIds.Contains(memberId) && s.SharedFields.Contains(ProfileShareFields.MemberNames), ct).ConfigureAwait(false);
                string? display = null;
                if (named)
                {
                    string first = await cipher.DecryptAsync(tenant.TenantId, member.FirstNameEnc, ct).ConfigureAwait(false) ?? "";
                    string last = await cipher.DecryptAsync(tenant.TenantId, member.LastNameEnc, ct).ConfigureAwait(false) ?? "";
                    string given = member.PreferredName ?? first;
                    display = last.Length > 0 ? $"{given} {char.ToUpperInvariant(last[0])}." : given;
                }

                roster.Add(new RosterEntry(e.Id, display));
            }

            return roster;
        }, cancellationToken);

    public Task<IReadOnlyList<FamilyEnrolment>> ForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<FamilyEnrolment>>(async ct =>
        {
            var mine = await (from e in db.Enrollments.AsNoTracking()
                              join m in db.FamilyMembers on e.FamilyMemberId equals m.Id
                              join p in db.FamilyProfiles on m.FamilyProfileId equals p.Id
                              where p.OwnerUserId == ownerUserId && e.SeatKind == EnrollmentKinds.Enrolled
                              orderby e.EnrolledAt descending
                              select new { e, m }).Take(200).ToListAsync(ct).ConfigureAwait(false);
            List<FamilyEnrolment> list = [];
            foreach (var x in mine)
            {
                list.Add(new FamilyEnrolment(x.e.Id, x.m.Id, await cipher.DecryptAsync(tenant.TenantId, x.m.FirstNameEnc, ct).ConfigureAwait(false) ?? "", x.e.ListingId, x.e.Status));
            }

            return list;
        }, cancellationToken);
}
