using System.Globalization;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Family;

public sealed record ShareInput(Guid OrganisationId, Guid? ListingId, IReadOnlyList<Guid> MemberIds, IReadOnlyList<string> Fields, DateTimeOffset? ExpiresAt);

/// <summary>A share as its family sees it.</summary>
public sealed record ShareView(
    Guid Id,
    Guid OrganisationId,
    string OrganisationName,
    Guid? ListingId,
    IReadOnlyList<Guid> MemberIds,
    IReadOnlyList<string> Fields,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string Status);

/// <summary>A shared member, holding only what the share covers; everything else is null.</summary>
[YouthData]
public sealed record SharedMemberView(Guid Id, string? Role, string? FirstName, string? LastName, string? PreferredName, DateOnly? DateOfBirth, short? Grade, string? Pronouns);

[YouthData]
public sealed record SharedHouseholdView(short? HouseholdSize, string? PrimaryLanguage, bool NeedsInterpreter);

/// <summary>What an organisation sees through one share.</summary>
[YouthData]
public sealed record SharedFamilyView(
    Guid ShareId,
    Guid? ListingId,
    IReadOnlyList<string> Fields,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    SharedHouseholdView? Household,
    IReadOnlyList<SharedMemberView> Members,
    IReadOnlyList<ContactView>? Contacts);

/// <summary>
/// Explicit, revocable sharing of a family profile with one organisation (M3a, D44): which members,
/// which fields, optionally for one listing, optionally until a date.
/// </summary>
/// <remarks>
/// <para>
/// Only the profile's owner grants or revokes. An organisation reads a share only while it is live
/// (neither revoked nor expired), only through a member of that organisation with the
/// youth-record permission (the caller checks that), and only the members and fields it names.
/// Every read is audited with the organisation as the actor's.
/// </para>
/// <para>
/// Support needs are never shown here, even under a share naming them: the table's own rule is that
/// only an organisation with an active enrolment reads them, and enrolments come later in M3a.
/// The family's choice is recorded now so the enrolment can honour it.
/// </para>
/// </remarks>
public sealed class ProfileShares(
    EnrollmentDbContext db,
    IFieldCipher cipher,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IDirectoryQueries directory,
    IListingDetails listings)
{
    public const int MaxLiveShares = 50;

    public const string OrganisationRead = "enrollment.family_share.read";

    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";

    /// <summary>The owner's shares, newest first, including ended ones so the family sees its history.</summary>
    public Task<IReadOnlyList<ShareView>> ListAsync(UserId owner, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ShareView>>(async ct =>
        {
            FamilyProfileRecord? profile = await ProfileAsync(owner, ct).ConfigureAwait(false);
            List<ProfileShareRecord> shares = profile is null
                ? []
                : await db.ProfileShares.AsNoTracking().Where(s => s.FamilyProfileId == profile.Id)
                    .OrderByDescending(s => s.GrantedAt).Take(200).ToListAsync(ct).ConfigureAwait(false);

            Dictionary<Guid, string> names = [];
            foreach (Guid org in shares.Select(s => s.OrgId).Distinct())
            {
                names[org] = await directory.GetOrganisationNameAsync(new OrganisationId(org), ct).ConfigureAwait(false) ?? "";
            }

            await AuditOwnerAsync(FamilyProfiles.Read, profile?.Id, "shares", ct).ConfigureAwait(false);
            return [.. shares.Select(s => View(s, names[s.OrgId]))];
        }, cancellationToken);

    /// <summary>
    /// Shares the profile with an organisation. A live share with the same organisation and listing
    /// is replaced, so changing what is shared is one call and the history keeps both.
    /// </summary>
    public Task<Result<ShareView>> GrantAsync(UserId owner, ShareInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            DateTimeOffset now = clock.UtcNow;
            if (input.Fields.Count == 0 || input.Fields.Any(f => !ProfileShareFields.All.Contains(f)))
            {
                return Result<ShareView>.Failure(Error.Validation("family.share_fields",
                    "One or more of " + string.Join(", ", ProfileShareFields.All.Order(StringComparer.Ordinal)) + "."));
            }

            if (input.ExpiresAt is { } expires && expires <= now)
            {
                return Result<ShareView>.Failure(Error.Validation("family.share_expiry", "An end date in the future, or none."));
            }

            if (await ProfileAsync(owner, ct).ConfigureAwait(false) is not { } profile)
            {
                return Result<ShareView>.Failure(Error.Precondition("family.no_profile", "Save the household's details first."));
            }

            List<Guid> members = [.. input.MemberIds.Distinct()];
            int known = await db.FamilyMembers.CountAsync(m => m.FamilyProfileId == profile.Id && members.Contains(m.Id), ct).ConfigureAwait(false);
            if (members.Count == 0 || known != members.Count)
            {
                return Result<ShareView>.Failure(Error.Validation("family.share_members", "One or more members of your household."));
            }

            OrganisationId org = new(input.OrganisationId);
            if (await directory.GetStatusAsync(org, ct).ConfigureAwait(false) is not OrganisationStatus.Approved
                || await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) is not { } orgName)
            {
                return Result<ShareView>.Failure(Error.Validation("family.share_org", "An approved organisation."));
            }

            if (input.ListingId is { } listingId
                && (await listings.GetForReviewAsync(tenant.TenantId, listingId, "en", ct).ConfigureAwait(false))?.OrganisationId != org)
            {
                return Result<ShareView>.Failure(Error.Validation("family.share_listing", "One of that organisation's activities, or none."));
            }

            ProfileShareRecord? replaced = await db.ProfileShares
                .FirstOrDefaultAsync(s => s.FamilyProfileId == profile.Id && s.OrgId == org.Value && s.ListingId == input.ListingId && s.RevokedAt == null, ct)
                .ConfigureAwait(false);
            if (replaced is null
                && await db.ProfileShares.CountAsync(s => s.FamilyProfileId == profile.Id && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now), ct).ConfigureAwait(false) >= MaxLiveShares)
            {
                return Result<ShareView>.Failure(Error.Conflict("family.too_many_shares", $"At most {MaxLiveShares} live shares."));
            }

            if (replaced is not null)
            {
                // Ended first, on its own: the partial unique index allows one live share per pair.
                replaced.RevokedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            ProfileShareRecord share = new()
            {
                Id = UuidV7.NewId(),
                TenantId = profile.TenantId,
                FamilyProfileId = profile.Id,
                OrgId = org.Value,
                ListingId = input.ListingId,
                MemberIds = [.. members],
                SharedFields = [.. input.Fields.Distinct().Order(StringComparer.Ordinal)],
                GrantedByUserId = owner.Value,
                GrantedAt = now,
                ExpiresAt = input.ExpiresAt,
            };
            db.ProfileShares.Add(share);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditOwnerAsync(FamilyProfiles.Changed, profile.Id, replaced is null ? "share_granted" : "share_replaced", ct, org.Value).ConfigureAwait(false);
            return Result<ShareView>.Success(View(share, orgName));
        }, cancellationToken);

    /// <summary>Ends a share at once. The organisation keeps nothing it read; it simply reads no more.</summary>
    public Task<Result> RevokeAsync(UserId owner, Guid shareId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ProfileShareRecord? share = await (from s in db.ProfileShares
                                               join p in db.FamilyProfiles on s.FamilyProfileId equals p.Id
                                               where s.Id == shareId && p.OwnerUserId == owner.Value && s.RevokedAt == null
                                               select s).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (share is null)
            {
                return Result.Failure(Error.NotFound("family.share_not_found", "You have no live share with that id."));
            }

            share.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditOwnerAsync(FamilyProfiles.Changed, share.FamilyProfileId, "share_revoked", ct, share.OrgId).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>
    /// The live shares an organisation holds, newest first, each read as through <see cref="ReadAsync"/>.
    /// The caller has checked the actor may read youth records for the organisation.
    /// </summary>
    public Task<IReadOnlyList<SharedFamilyView>> ForOrganisationAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<SharedFamilyView>>(async ct =>
        {
            DateTimeOffset now = clock.UtcNow;
            List<ProfileShareRecord> shares = await db.ProfileShares.AsNoTracking()
                .Where(s => s.OrgId == org.Value && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now))
                .OrderByDescending(s => s.GrantedAt).Take(200).ToListAsync(ct).ConfigureAwait(false);

            List<SharedFamilyView> views = [];
            foreach (ProfileShareRecord share in shares)
            {
                views.Add(await SharedAsync(share, ct).ConfigureAwait(false));
            }

            await AuditOrganisationAsync(org, [.. shares.Select(s => s.FamilyProfileId).Distinct()], "list", ct).ConfigureAwait(false);
            return views;
        }, cancellationToken);

    /// <summary>One share, read by the organisation it was given to.</summary>
    public Task<Result<SharedFamilyView>> ReadAsync(OrganisationId org, Guid shareId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ProfileShareRecord? share = await db.ProfileShares.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == shareId && s.OrgId == org.Value, ct).ConfigureAwait(false);
            if (share is null)
            {
                return Result<SharedFamilyView>.Failure(Error.NotFound("family.share_not_found", "This organisation holds no share with that id."));
            }

            if (Status(share, clock.UtcNow) != Active)
            {
                return Result<SharedFamilyView>.Failure(Error.Forbidden("family.share_ended", "The family has ended this share, or it has run out."));
            }

            SharedFamilyView view = await SharedAsync(share, ct).ConfigureAwait(false);
            await AuditOrganisationAsync(org, [share.FamilyProfileId], string.Join(',', share.SharedFields), ct, share.Id).ConfigureAwait(false);
            return Result<SharedFamilyView>.Success(view);
        }, cancellationToken);

    // ---- reading through a share -------------------------------------------------------------

    private async Task<SharedFamilyView> SharedAsync(ProfileShareRecord share, CancellationToken ct)
    {
        HashSet<string> fields = new(share.SharedFields, StringComparer.Ordinal);
        FamilyProfileRecord profile = await db.FamilyProfiles.AsNoTracking().FirstAsync(p => p.Id == share.FamilyProfileId, ct).ConfigureAwait(false);

        // Members removed since the share was made drop out here.
        Guid[] ids = share.MemberIds;
        List<FamilyMemberRecord> members = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyProfileId == profile.Id && ids.Contains(m.Id)).OrderBy(m => m.CreatedAt).ToListAsync(ct).ConfigureAwait(false);

        List<SharedMemberView> memberViews = [];
        foreach (FamilyMemberRecord m in members)
        {
            bool names = fields.Contains(ProfileShareFields.MemberNames);
            string? dob = fields.Contains(ProfileShareFields.MemberBirthDates) ? await cipher.DecryptAsync(tenant.TenantId, m.DateOfBirthEnc, ct).ConfigureAwait(false) : null;
            memberViews.Add(new SharedMemberView(
                m.Id,
                names ? m.MemberRole : null,
                names ? await cipher.DecryptAsync(tenant.TenantId, m.FirstNameEnc, ct).ConfigureAwait(false) : null,
                names ? await cipher.DecryptAsync(tenant.TenantId, m.LastNameEnc, ct).ConfigureAwait(false) : null,
                names ? m.PreferredName : null,
                dob is null ? null : DateOnly.ParseExact(dob, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                fields.Contains(ProfileShareFields.MemberGrade) ? m.Grade : null,
                fields.Contains(ProfileShareFields.MemberPronouns) ? m.Pronouns : null));
        }

        List<ContactView>? contacts = null;
        if (fields.Contains(ProfileShareFields.Contacts))
        {
            contacts = [];
            foreach (FamilyContactRecord c in await db.FamilyContacts.AsNoTracking().Where(c => c.FamilyProfileId == profile.Id).OrderBy(c => c.SortOrder).ToListAsync(ct).ConfigureAwait(false))
            {
                contacts.Add(new ContactView(
                    c.Id,
                    await cipher.DecryptAsync(tenant.TenantId, c.NameEnc, ct).ConfigureAwait(false) ?? "",
                    c.Relationship,
                    await cipher.DecryptAsync(tenant.TenantId, c.PhoneEnc, ct).ConfigureAwait(false) ?? "",
                    await cipher.DecryptAsync(tenant.TenantId, c.EmailEnc, ct).ConfigureAwait(false),
                    c.IsEmergency,
                    c.CanPickUp));
            }
        }

        return new SharedFamilyView(
            share.Id,
            share.ListingId,
            share.SharedFields,
            share.GrantedAt,
            share.ExpiresAt,
            fields.Contains(ProfileShareFields.Household) ? new SharedHouseholdView(profile.HouseholdSize, profile.PrimaryLanguage, profile.NeedsInterpreter) : null,
            memberViews,
            contacts);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private Task<FamilyProfileRecord?> ProfileAsync(UserId owner, CancellationToken ct) =>
        db.FamilyProfiles.FirstOrDefaultAsync(p => p.OwnerUserId == owner.Value, ct);

    private static string Status(ProfileShareRecord share, DateTimeOffset now) =>
        share.RevokedAt is not null ? Revoked
        : share.ExpiresAt is { } ends && ends <= now ? Expired
        : Active;

    private ShareView View(ProfileShareRecord share, string organisationName) => new(
        share.Id, share.OrgId, organisationName, share.ListingId, share.MemberIds, share.SharedFields,
        share.GrantedAt, share.ExpiresAt, share.RevokedAt, Status(share, clock.UtcNow));

    private Task AuditOwnerAsync(string action, Guid? profile, string change, CancellationToken ct, Guid? org = null)
    {
        Dictionary<string, string> detail = new(StringComparer.Ordinal) { ["change"] = change };
        if (org is { } o)
        {
            detail["org"] = o.ToString();
        }

        return audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "family_profile",
            SubjectIds = profile is { } id ? [id] : [],
            Detail = detail,
        }, ct);
    }

    private Task AuditOrganisationAsync(OrganisationId org, IReadOnlyCollection<Guid> profiles, string fields, CancellationToken ct, Guid? share = null)
    {
        Dictionary<string, string> detail = new(StringComparer.Ordinal) { ["fields"] = fields };
        if (share is { } s)
        {
            detail["share"] = s.ToString();
        }

        return audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = OrganisationRead,
            SubjectKind = "family_profile",
            SubjectIds = profiles,
            ActorOrgId = org,
            Detail = detail,
        }, ct);
    }
}
