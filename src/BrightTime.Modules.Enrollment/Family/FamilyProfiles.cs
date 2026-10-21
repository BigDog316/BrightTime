using System.Globalization;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Enrollment.Family;

public sealed record FamilyInput(short? HouseholdSize, string? PrimaryLanguage, bool NeedsInterpreter);

public sealed record MemberInput(
    string Role,
    string FirstName,
    string LastName,
    string? PreferredName,
    DateOnly? DateOfBirth,
    short? Grade,
    string? Pronouns,
    string? Relationship,
    Guid? NeighbourhoodId = null);

public sealed record SupportNeedInput(string Kind, string? Detail, bool ShareWithProviders);

public sealed record ContactInput(string Name, string Relationship, string Phone, string? Email, bool IsEmergency, bool CanPickUp);

[YouthData]
public sealed record SupportNeedView(Guid Id, string Kind, string? Detail, bool ShareWithProviders);

[YouthData]
public sealed record MemberView(
    Guid Id,
    string Role,
    string FirstName,
    string LastName,
    string? PreferredName,
    DateOnly? DateOfBirth,
    short? Grade,
    string? Pronouns,
    string? Relationship,
    Guid? NeighbourhoodId,
    IReadOnlyList<SupportNeedView> SupportNeeds);

[YouthData]
public sealed record ContactView(Guid Id, string Name, string Relationship, string Phone, string? Email, bool IsEmergency, bool CanPickUp);

[YouthData]
public sealed record FamilyView(Guid Id, short? HouseholdSize, string? PrimaryLanguage, bool NeedsInterpreter, IReadOnlyList<MemberView> Members, IReadOnlyList<ContactView> Contacts);

/// <summary>
/// The family profile (M3a, D43): a household's members, their support needs and its contacts,
/// kept once so a family applies to a second program without typing it all again.
/// </summary>
/// <remarks>
/// <para>
/// Owned by the account that made it, and read or changed by that account only, decided here
/// record by record (D4). Organisations see a profile only through an explicit, revocable share
/// (<c>profile_share</c>, with applications), never through this service.
/// </para>
/// <para>
/// Names, dates of birth, support details, contact names, phones and emails are encrypted under
/// the tenant's data key (D32); only the birth month is kept in the clear, as for accounts. Every
/// read and every change is one audit row (docs/plan/07: "audit every read and write of youth
/// records").
/// </para>
/// </remarks>
public sealed class FamilyProfiles(EnrollmentDbContext db, IFieldCipher cipher, IBlindIndexer blind, ITenantContext tenant, AuditTrail audit, IClock clock, IGeographyQueries geography)
{
    public const int MaxMembers = 15;
    public const int MaxContacts = 10;
    public const int MaxSupportNeeds = 10;
    public const int MaxNameLength = 100;
    public const int MaxDetailLength = 500;

    public const string Read = "enrollment.family.read";
    public const string Changed = "enrollment.family.changed";

    /// <summary>The owner's profile, decrypted; null when they have none.</summary>
    public Task<FamilyView?> GetAsync(UserId owner, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<FamilyView?>(async ct =>
        {
            FamilyProfileRecord? profile = await ProfileAsync(owner, ct).ConfigureAwait(false);
            if (profile is null)
            {
                await AuditAsync(Read, null, ct).ConfigureAwait(false);
                return null;
            }

            FamilyView view = await ViewAsync(profile, ct).ConfigureAwait(false);
            await AuditAsync(Read, profile.Id, ct).ConfigureAwait(false);
            return view;
        }, cancellationToken);

    /// <summary>Makes the owner's profile, or changes its household details.</summary>
    public Task<Result<FamilyView>> SaveAsync(UserId owner, FamilyInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (input.HouseholdSize is < 1 or > 30)
            {
                return Result<FamilyView>.Failure(Error.Validation("family.household_size", "A household of 1 to 30."));
            }

            string? language = input.PrimaryLanguage?.Trim();
            if (language is { Length: > 35 } || (language is { Length: > 0 } && !Locale.TryResolve(language, out _) && !language.All(c => char.IsAsciiLetter(c) || c == '-')))
            {
                return Result<FamilyView>.Failure(Error.Validation("family.language", "A language tag, such as es or zh-Hans."));
            }

            DateTimeOffset now = clock.StoredNow();
            FamilyProfileRecord profile = await ProfileAsync(owner, ct).ConfigureAwait(false) ?? Add(new FamilyProfileRecord
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OwnerUserId = owner.Value,
                CreatedAt = now,
            });
            profile.HouseholdSize = input.HouseholdSize;
            profile.PrimaryLanguage = language is { Length: > 0 } ? language : null;
            profile.NeedsInterpreter = input.NeedsInterpreter;
            profile.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, profile.Id, ct, "profile").ConfigureAwait(false);
            return Result<FamilyView>.Success(await ViewAsync(profile, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<MemberView>> AddMemberAsync(UserId owner, MemberInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if ((Check(input) ?? await NeighbourhoodAsync(input, ct).ConfigureAwait(false)) is { } invalid)
            {
                return Result<MemberView>.Failure(invalid);
            }

            if (await ProfileAsync(owner, ct).ConfigureAwait(false) is not { } profile)
            {
                return Result<MemberView>.Failure(NoProfile());
            }

            if (await db.FamilyMembers.CountAsync(m => m.FamilyProfileId == profile.Id, ct).ConfigureAwait(false) >= MaxMembers)
            {
                return Result<MemberView>.Failure(Error.Conflict("family.too_many_members", $"At most {MaxMembers} members."));
            }

            DateTimeOffset now = clock.StoredNow();
            FamilyMemberRecord member = new()
            {
                Id = UuidV7.NewId(),
                TenantId = profile.TenantId,
                FamilyProfileId = profile.Id,
                MemberRole = input.Role,
                FirstNameEnc = [],
                LastNameEnc = [],
                CreatedAt = now,
            };
            await ApplyAsync(member, input, now, ct).ConfigureAwait(false);
            db.FamilyMembers.Add(member);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, profile.Id, ct, "member_added").ConfigureAwait(false);
            return Result<MemberView>.Success(await MemberAsync(member, [], ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<MemberView>> UpdateMemberAsync(UserId owner, Guid memberId, MemberInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if ((Check(input) ?? await NeighbourhoodAsync(input, ct).ConfigureAwait(false)) is { } invalid)
            {
                return Result<MemberView>.Failure(invalid);
            }

            if (await OwnMemberAsync(owner, memberId, ct).ConfigureAwait(false) is not { } member)
            {
                return Result<MemberView>.Failure(NoMember());
            }

            member.MemberRole = input.Role;
            await ApplyAsync(member, input, clock.StoredNow(), ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, member.FamilyProfileId, ct, "member_changed").ConfigureAwait(false);
            List<SupportNeedRecord> needs = await db.SupportNeeds.AsNoTracking().Where(n => n.FamilyMemberId == member.Id).ToListAsync(ct).ConfigureAwait(false);
            return Result<MemberView>.Success(await MemberAsync(member, needs, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Removes a member, and with them their support needs.</summary>
    public Task<Result> RemoveMemberAsync(UserId owner, Guid memberId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnMemberAsync(owner, memberId, ct).ConfigureAwait(false) is not { } member)
            {
                return Result.Failure(NoMember());
            }

            db.FamilyMembers.Remove(member);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, member.FamilyProfileId, ct, "member_removed").ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>
    /// Replaces a member's support needs. Each says whether a program the family enrols with may
    /// see it; none is ever in an export, report or aggregate (the table's own rule).
    /// </summary>
    public Task<Result<MemberView>> SetSupportNeedsAsync(UserId owner, Guid memberId, IReadOnlyList<SupportNeedInput> needs, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(needs);
            if (needs.Count > MaxSupportNeeds)
            {
                return Result<MemberView>.Failure(Error.Validation("family.too_many_needs", $"At most {MaxSupportNeeds} support needs."));
            }

            if (needs.Any(n => !SupportNeedKinds.All.Contains(n.Kind)))
            {
                return Result<MemberView>.Failure(Error.Validation("family.need_kind", "One of " + string.Join(", ", SupportNeedKinds.All.Order(StringComparer.Ordinal)) + "."));
            }

            if (needs.Any(n => n.Detail is { Length: > MaxDetailLength }))
            {
                return Result<MemberView>.Failure(Error.Validation("family.need_detail", $"At most {MaxDetailLength} characters."));
            }

            if (await OwnMemberAsync(owner, memberId, ct).ConfigureAwait(false) is not { } member)
            {
                return Result<MemberView>.Failure(NoMember());
            }

            await db.SupportNeeds.Where(n => n.FamilyMemberId == member.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            List<SupportNeedRecord> saved = [];
            foreach (SupportNeedInput need in needs)
            {
                SupportNeedRecord record = new()
                {
                    Id = UuidV7.NewId(),
                    TenantId = member.TenantId,
                    FamilyMemberId = member.Id,
                    NeedKind = need.Kind,
                    DetailEnc = await cipher.EncryptAsync(tenant.TenantId, Blank(need.Detail), ct).ConfigureAwait(false),
                    ShareWithProviders = need.ShareWithProviders,
                    CreatedAt = now,
                };
                db.SupportNeeds.Add(record);
                saved.Add(record);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, member.FamilyProfileId, ct, "support_needs").ConfigureAwait(false);
            return Result<MemberView>.Success(await MemberAsync(member, saved, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<ContactView>> AddContactAsync(UserId owner, ContactInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Check(input) is { } invalid)
            {
                return Result<ContactView>.Failure(invalid);
            }

            if (await ProfileAsync(owner, ct).ConfigureAwait(false) is not { } profile)
            {
                return Result<ContactView>.Failure(NoProfile());
            }

            int count = await db.FamilyContacts.CountAsync(c => c.FamilyProfileId == profile.Id, ct).ConfigureAwait(false);
            if (count >= MaxContacts)
            {
                return Result<ContactView>.Failure(Error.Conflict("family.too_many_contacts", $"At most {MaxContacts} contacts."));
            }

            FamilyContactRecord contact = new()
            {
                Id = UuidV7.NewId(),
                TenantId = profile.TenantId,
                FamilyProfileId = profile.Id,
                NameEnc = [],
                Relationship = "",
                PhoneEnc = [],
                SortOrder = (short)count,
            };
            await ApplyAsync(contact, input, ct).ConfigureAwait(false);
            db.FamilyContacts.Add(contact);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, profile.Id, ct, "contact_added").ConfigureAwait(false);
            return Result<ContactView>.Success(await ContactAsync(contact, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<ContactView>> UpdateContactAsync(UserId owner, Guid contactId, ContactInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Check(input) is { } invalid)
            {
                return Result<ContactView>.Failure(invalid);
            }

            if (await OwnContactAsync(owner, contactId, ct).ConfigureAwait(false) is not { } contact)
            {
                return Result<ContactView>.Failure(NoContact());
            }

            await ApplyAsync(contact, input, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, contact.FamilyProfileId, ct, "contact_changed").ConfigureAwait(false);
            return Result<ContactView>.Success(await ContactAsync(contact, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result> RemoveContactAsync(UserId owner, Guid contactId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnContactAsync(owner, contactId, ct).ConfigureAwait(false) is not { } contact)
            {
                return Result.Failure(NoContact());
            }

            db.FamilyContacts.Remove(contact);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, contact.FamilyProfileId, ct, "contact_removed").ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- checks ------------------------------------------------------------------------------

    private Error? Check(MemberInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!FamilyMemberRoles.All.Contains(input.Role))
        {
            return Error.Validation("family.member_role", "One of youth, child, guardian, emergency_contact.");
        }

        if (!Named(input.FirstName) || !Named(input.LastName) || (input.PreferredName is { } p && (p.Length > 60 || p.Any(char.IsControl))))
        {
            return Error.Validation("family.member_name", $"A first and last name of 1 to {MaxNameLength} characters.");
        }

        DateOnly today = clock.TodayIn(tenant.TimeZone);
        if (FamilyMemberRoles.IsYoungPerson(input.Role) && input.DateOfBirth is null)
        {
            return Error.Validation("family.member_birth_date", "A young person's date of birth, which only programs that need it to check ages see.");
        }

        if (input.DateOfBirth is { } dob && (dob > today || dob < today.AddYears(-120)))
        {
            return Error.Validation("family.member_birth_date", "A date of birth that is not in the future.");
        }

        if (input.Grade is < -1 or > 12)
        {
            return Error.Validation("family.member_grade", "A grade from -1 (pre-K) to 12.");
        }

        return input.Pronouns is { Length: > 40 } || input.Relationship is { Length: > 40 }
            ? Error.Validation("family.member_detail", "Pronouns and relationship of at most 40 characters.")
            : null;
    }

    private static Error? Check(ContactInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Named(input.Name) || input.Relationship is not { Length: > 0 and <= 40 })
        {
            return Error.Validation("family.contact_name", "A name, and how they are related, of at most 100 and 40 characters.");
        }

        string digits = new([.. input.Phone.Where(char.IsAsciiDigit)]);
        if (digits.Length is < 7 or > 15)
        {
            return Error.Validation("family.contact_phone", "A phone number of 7 to 15 digits.");
        }

        return input.Email is { Length: > 0 } email && (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            ? Error.Validation("family.contact_email", "An email address, or none.")
            : null;
    }

    /// <summary>
    /// A member's neighbourhood, one of the tenant's own: coarse enough for eligibility
    /// ("lives in Eastgate") and never an address (docs/plan/07).
    /// </summary>
    private async Task<Error?> NeighbourhoodAsync(MemberInput input, CancellationToken ct) =>
        input.NeighbourhoodId is { } id && !(await geography.GetNeighbourhoodNamesAsync([id], Locale.Default.Code, ct).ConfigureAwait(false)).ContainsKey(id)
            ? Error.Validation("family.member_neighbourhood", "One of the county's neighbourhoods, or none.")
            : null;

    private static bool Named(string? name) =>
        name?.Trim() is { Length: > 0 and <= MaxNameLength } trimmed && !trimmed.Any(char.IsControl);

    // ---- writes ------------------------------------------------------------------------------

    private async Task ApplyAsync(FamilyMemberRecord member, MemberInput input, DateTimeOffset now, CancellationToken ct)
    {
        member.FirstNameEnc = (await cipher.EncryptAsync(tenant.TenantId, input.FirstName.Trim(), ct).ConfigureAwait(false))!;
        member.LastNameEnc = (await cipher.EncryptAsync(tenant.TenantId, input.LastName.Trim(), ct).ConfigureAwait(false))!;
        member.PreferredName = Blank(input.PreferredName);
        member.DateOfBirthEnc = input.DateOfBirth is { } dob
            ? await cipher.EncryptAsync(tenant.TenantId, dob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct).ConfigureAwait(false)
            : null;
        member.BirthMonth = input.DateOfBirth is { } d ? Age.ToBirthMonth(d) : null;
        member.Grade = input.Grade;
        member.Pronouns = Blank(input.Pronouns);
        member.RelationshipToOwner = Blank(input.Relationship);
        member.NeighbourhoodId = input.NeighbourhoodId;
        member.UpdatedAt = now;
    }

    private async Task ApplyAsync(FamilyContactRecord contact, ContactInput input, CancellationToken ct)
    {
        string phone = input.Phone.Trim();
        contact.NameEnc = (await cipher.EncryptAsync(tenant.TenantId, input.Name.Trim(), ct).ConfigureAwait(false))!;
        contact.Relationship = input.Relationship.Trim();
        contact.PhoneEnc = (await cipher.EncryptAsync(tenant.TenantId, phone, ct).ConfigureAwait(false))!;
        // Exact-match lookup by number (a provider finding a contact) without decrypting every row.
        contact.PhoneBlindIndex = await blind.ComputeAsync(tenant.TenantId, new string([.. phone.Where(char.IsAsciiDigit)]), ct).ConfigureAwait(false);
        contact.EmailEnc = await cipher.EncryptAsync(tenant.TenantId, Blank(input.Email), ct).ConfigureAwait(false);
        contact.IsEmergency = input.IsEmergency;
        contact.CanPickUp = input.CanPickUp;
    }

    private FamilyProfileRecord Add(FamilyProfileRecord profile)
    {
        db.FamilyProfiles.Add(profile);
        return profile;
    }

    // ---- reads -------------------------------------------------------------------------------

    private Task<FamilyProfileRecord?> ProfileAsync(UserId owner, CancellationToken ct) =>
        db.FamilyProfiles.FirstOrDefaultAsync(p => p.OwnerUserId == owner.Value, ct);

    private async Task<FamilyMemberRecord?> OwnMemberAsync(UserId owner, Guid memberId, CancellationToken ct) =>
        await (from m in db.FamilyMembers
               join p in db.FamilyProfiles on m.FamilyProfileId equals p.Id
               where m.Id == memberId && p.OwnerUserId == owner.Value
               select m).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<FamilyContactRecord?> OwnContactAsync(UserId owner, Guid contactId, CancellationToken ct) =>
        await (from c in db.FamilyContacts
               join p in db.FamilyProfiles on c.FamilyProfileId equals p.Id
               where c.Id == contactId && p.OwnerUserId == owner.Value
               select c).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<FamilyView> ViewAsync(FamilyProfileRecord profile, CancellationToken ct)
    {
        List<FamilyMemberRecord> members = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyProfileId == profile.Id).OrderBy(m => m.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        List<Guid> ids = [.. members.Select(m => m.Id)];
        ILookup<Guid, SupportNeedRecord> needs = (await db.SupportNeeds.AsNoTracking()
            .Where(n => ids.Contains(n.FamilyMemberId)).OrderBy(n => n.CreatedAt).ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(n => n.FamilyMemberId);
        List<FamilyContactRecord> contacts = await db.FamilyContacts.AsNoTracking()
            .Where(c => c.FamilyProfileId == profile.Id).OrderBy(c => c.SortOrder).ToListAsync(ct).ConfigureAwait(false);

        List<MemberView> memberViews = [];
        foreach (FamilyMemberRecord member in members)
        {
            memberViews.Add(await MemberAsync(member, [.. needs[member.Id]], ct).ConfigureAwait(false));
        }

        List<ContactView> contactViews = [];
        foreach (FamilyContactRecord contact in contacts)
        {
            contactViews.Add(await ContactAsync(contact, ct).ConfigureAwait(false));
        }

        return new FamilyView(profile.Id, profile.HouseholdSize, profile.PrimaryLanguage, profile.NeedsInterpreter, memberViews, contactViews);
    }

    private async Task<MemberView> MemberAsync(FamilyMemberRecord member, IReadOnlyList<SupportNeedRecord> needs, CancellationToken ct)
    {
        List<SupportNeedView> needViews = [];
        foreach (SupportNeedRecord need in needs)
        {
            needViews.Add(new SupportNeedView(need.Id, need.NeedKind, await cipher.DecryptAsync(tenant.TenantId, need.DetailEnc, ct).ConfigureAwait(false), need.ShareWithProviders));
        }

        string? dob = await cipher.DecryptAsync(tenant.TenantId, member.DateOfBirthEnc, ct).ConfigureAwait(false);
        return new MemberView(
            member.Id,
            member.MemberRole,
            await cipher.DecryptAsync(tenant.TenantId, member.FirstNameEnc, ct).ConfigureAwait(false) ?? "",
            await cipher.DecryptAsync(tenant.TenantId, member.LastNameEnc, ct).ConfigureAwait(false) ?? "",
            member.PreferredName,
            dob is null ? null : DateOnly.ParseExact(dob, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            member.Grade,
            member.Pronouns,
            member.RelationshipToOwner,
            member.NeighbourhoodId,
            needViews);
    }

    private async Task<ContactView> ContactAsync(FamilyContactRecord contact, CancellationToken ct) => new(
        contact.Id,
        await cipher.DecryptAsync(tenant.TenantId, contact.NameEnc, ct).ConfigureAwait(false) ?? "",
        contact.Relationship,
        await cipher.DecryptAsync(tenant.TenantId, contact.PhoneEnc, ct).ConfigureAwait(false) ?? "",
        await cipher.DecryptAsync(tenant.TenantId, contact.EmailEnc, ct).ConfigureAwait(false),
        contact.IsEmergency,
        contact.CanPickUp);

    private Task AuditAsync(string action, Guid? profile, CancellationToken ct, string? what = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "family_profile",
            SubjectIds = profile is { } id ? [id] : [],
            Detail = what is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.Ordinal) { ["change"] = what },
        }, ct);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Error NoProfile() => Error.Precondition("family.no_profile", "Save the household's details first.");

    private static Error NoMember() => Error.NotFound("family.member_not_found", "Your household has no member with that id.");

    private static Error NoContact() => Error.NotFound("family.contact_not_found", "Your household has no contact with that id.");
}

/// <summary>Registration of the Enrollment module.</summary>
public static class EnrollmentModule
{
    /// <summary>The request-side services. The host registers <see cref="EnrollmentDbContext"/> and field encryption.</summary>
    public static IServiceCollection AddEnrollment(this IServiceCollection services)
    {
        services.AddScoped<FamilyProfiles>();
        services.AddScoped<ProfileShares>();
        services.AddScoped<Forms.FormDefinitions>();
        services.AddScoped<Eligibility.EligibilityRules>();
        services.AddScoped<Applications.Applications>();
        services.AddScoped<Waivers.Waivers>();
        services.AddScoped<Applications.SealedAnswers>();
        services.AddScoped<Decisions.SeatLedger>();
        services.AddScoped<Decisions.Decisions>();
        return services;
    }
}
