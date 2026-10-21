using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Waivers;

public sealed record WaiverSummary(Guid Id, string Title, int Version, string Status, bool RequiresGuardian, DateTimeOffset CreatedAt);

public sealed record WaiverListing(Guid ListingId, bool IsRequired);

public sealed record WaiverView(
    Guid Id,
    string Title,
    int Version,
    string Status,
    string BodyMarkdown,
    string Hash,
    bool RequiresGuardian,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WaiverListing> Listings,
    int SignatureCount);

/// <summary>A waiver as a family meets it on an application: the text to read, and whether it is signed.</summary>
[YouthData]
public sealed record ApplicationWaiver(Guid WaiverId, string Title, int Version, string BodyMarkdown, string Hash, bool IsRequired, DateTimeOffset? SignedAt);

[YouthData]
public sealed record SignatureView(Guid Id, Guid WaiverId, string SignerRole, string TypedName, string Hash, DateTimeOffset SignedAt);

/// <summary>
/// Waivers with e-signature (M3a, D47): written and versioned by an organisation, attached to its
/// listings, and signed by a family for one application.
/// </summary>
/// <remarks>
/// <para>
/// A signature is evidence: who signed, as what, for which young person, the SHA-256 of the exact
/// text they were shown, when, and hashes of the address and browser they signed from. The signer
/// sends back the hash of the text they read, so a signature can only be for the words the
/// organisation published; a published version's text never changes, and a new version needs
/// a new signature. Signatures are append-only in the database.
/// </para>
/// <para>
/// Who signs is decided by the young person's age: for someone under 18, an adult account
/// holding them on its family profile signs, as their guardian. Someone 18 or over signs for
/// themselves, which waits on linking a member to their own account. <c>requires_guardian</c> is
/// kept and shown, and does not override age.
/// </para>
/// </remarks>
public sealed class Waivers(
    EnrollmentDbContext db,
    IFieldCipher cipher,
    ITenantContext tenant,
    AuditTrail audit,
    IAuditRequestInfo request,
    IClock clock,
    IListingDetails listings)
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 20_000;

    public const string Draft = "draft";
    public const string Published = "published";
    public const string Retired = "retired";

    public const string SignedAction = "enrollment.waiver.signed";

    // ---- the organisation's side -------------------------------------------------------------

    public Task<IReadOnlyList<WaiverSummary>> ListAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<WaiverSummary>>(async ct =>
            [.. (await db.Waivers.AsNoTracking().Where(w => w.OrgId == org.Value).OrderBy(w => w.Title).ThenByDescending(w => w.Version).ToListAsync(ct).ConfigureAwait(false))
                .Select(w => new WaiverSummary(w.Id, w.Title, w.Version, w.Status, w.RequiresGuardian, w.CreatedAt))], cancellationToken);

    public Task<Result<WaiverView>> GetAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await OwnAsync(org, id, ct).ConfigureAwait(false) is { } waiver
                ? Result<WaiverView>.Success(await ViewAsync(waiver, ct).ConfigureAwait(false))
                : Result<WaiverView>.Failure(NotFound()), cancellationToken);

    /// <summary>A new waiver, as version 1 of a draft. The title stays with it through its versions.</summary>
    public Task<Result<WaiverView>> CreateAsync(OrganisationId org, string title, string body, bool requiresGuardian, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? name = title?.Trim();
            if (name is not { Length: > 0 and <= MaxTitleLength } || name.Any(char.IsControl))
            {
                return Result<WaiverView>.Failure(Error.Validation("waiver.title", $"A title of 1 to {MaxTitleLength} characters."));
            }

            if (Body(body) is not { } text)
            {
                return Result<WaiverView>.Failure(BadBody());
            }

            if (await db.Waivers.AnyAsync(w => w.OrgId == org.Value && w.Title == name, ct).ConfigureAwait(false))
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.title_taken", "Your organisation already has a waiver with that title."));
            }

            WaiverRecord waiver = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                Title = name,
                BodyMd = text,
                BodyHash = Hash(text),
                Version = 1,
                RequiresGuardian = requiresGuardian,
                Status = Draft,
                CreatedAt = clock.StoredNow(),
            };
            db.Waivers.Add(waiver);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<WaiverView>.Success(await ViewAsync(waiver, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<WaiverView>> UpdateAsync(OrganisationId org, Guid id, string body, bool requiresGuardian, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } waiver)
            {
                return Result<WaiverView>.Failure(NotFound());
            }

            if (waiver.Status != Draft)
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.not_draft", "A published waiver never changes. Make a new version to change it."));
            }

            if (Body(body) is not { } text)
            {
                return Result<WaiverView>.Failure(BadBody());
            }

            waiver.BodyMd = text;
            waiver.BodyHash = Hash(text);
            waiver.RequiresGuardian = requiresGuardian;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<WaiverView>.Success(await ViewAsync(waiver, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<WaiverView>> PublishAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } waiver)
            {
                return Result<WaiverView>.Failure(NotFound());
            }

            if (waiver.Status != Draft)
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.not_draft", "Only a draft can be published."));
            }

            waiver.Status = Published;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<WaiverView>.Success(await ViewAsync(waiver, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>The next version of a published or retired waiver, as a draft holding its text.</summary>
    public Task<Result<WaiverView>> ReviseAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } source)
            {
                return Result<WaiverView>.Failure(NotFound());
            }

            if (source.Status == Draft)
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.not_published", "A draft can be changed as it is."));
            }

            List<WaiverRecord> versions = await db.Waivers.Where(w => w.OrgId == org.Value && w.Title == source.Title).ToListAsync(ct).ConfigureAwait(false);
            if (versions.Any(w => w.Status == Draft))
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.draft_exists", "This waiver already has a draft of its next version."));
            }

            WaiverRecord next = new()
            {
                Id = UuidV7.NewId(),
                TenantId = source.TenantId,
                OrgId = source.OrgId,
                Title = source.Title,
                BodyMd = source.BodyMd,
                BodyHash = source.BodyHash,
                Version = versions.Max(w => w.Version) + 1,
                RequiresGuardian = source.RequiresGuardian,
                Status = Draft,
                CreatedAt = clock.StoredNow(),
            };
            db.Waivers.Add(next);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<WaiverView>.Success(await ViewAsync(next, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Stops a version being attached anywhere new. Refused while a listing still asks for it. Its signatures stay.</summary>
    public Task<Result<WaiverView>> RetireAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } waiver)
            {
                return Result<WaiverView>.Failure(NotFound());
            }

            if (waiver.Status != Published)
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.not_published", "Only a published waiver can be retired."));
            }

            if (await db.ListingWaivers.AnyAsync(l => l.WaiverId == waiver.Id, ct).ConfigureAwait(false))
            {
                return Result<WaiverView>.Failure(Error.Conflict("waiver.in_use", "A listing still asks families to sign this. Take it off first."));
            }

            waiver.Status = Retired;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<WaiverView>.Success(await ViewAsync(waiver, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Asks a listing's families to sign a published waiver, required or not; again, it changes which.</summary>
    public Task<Result> AttachAsync(OrganisationId org, Guid listingId, Guid waiverId, bool isRequired, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            if (await OwnAsync(org, waiverId, ct).ConfigureAwait(false) is not { } waiver)
            {
                return Result.Failure(NotFound());
            }

            if (waiver.Status != Published)
            {
                return Result.Failure(Error.Conflict("waiver.not_published", "Publish the waiver before a listing uses it."));
            }

            ListingWaiverRecord? current = await db.ListingWaivers.FirstOrDefaultAsync(l => l.ListingId == listingId && l.WaiverId == waiverId, ct).ConfigureAwait(false);
            if (current is null)
            {
                db.ListingWaivers.Add(new ListingWaiverRecord { ListingId = listingId, WaiverId = waiverId, IsRequired = isRequired });
            }
            else
            {
                current.IsRequired = isRequired;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<Result> DetachAsync(OrganisationId org, Guid listingId, Guid waiverId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            await db.ListingWaivers.Where(l => l.ListingId == listingId && l.WaiverId == waiverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- the family's side -------------------------------------------------------------------

    /// <summary>The waivers an application's listing asks for, each with whether this young person has signed that text.</summary>
    public Task<Result<IReadOnlyList<ApplicationWaiver>>> ForApplicationAsync(UserId owner, Guid applicationId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnApplicationAsync(owner, applicationId, ct).ConfigureAwait(false) is not { } application)
            {
                return Result<IReadOnlyList<ApplicationWaiver>>.Failure(Error.NotFound("application.not_found", "You have no application with that id."));
            }

            List<(WaiverRecord Waiver, bool IsRequired)> asked = await AskedAsync(application.ListingId, ct).ConfigureAwait(false);
            List<WaiverSignatureRecord> signatures = await SignaturesAsync(application.FamilyMemberId, asked.Select(a => a.Waiver.Id), ct).ConfigureAwait(false);
            List<ApplicationWaiver> views = [.. asked.Select(a => new ApplicationWaiver(
                a.Waiver.Id, a.Waiver.Title, a.Waiver.Version, a.Waiver.BodyMd, Hex(a.Waiver.BodyHash), a.IsRequired,
                signatures.FirstOrDefault(s => s.WaiverId == a.Waiver.Id && s.BodyHashSigned.AsSpan().SequenceEqual(a.Waiver.BodyHash))?.SignedAt))];
            await AuditAsync(Applications.Applications.Read, application.Id, null, ct).ConfigureAwait(false);
            return Result<IReadOnlyList<ApplicationWaiver>>.Success(views);
        }, cancellationToken);

    /// <summary>
    /// Signs a waiver for an application's young person. <paramref name="hash"/> is the SHA-256 the
    /// signer was shown with the text; signing the same text twice returns the first signature.
    /// </summary>
    public Task<Result<SignatureView>> SignAsync(UserId owner, bool ownerIsAdult, Guid applicationId, Guid waiverId, string typedName, string hash, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnApplicationAsync(owner, applicationId, ct).ConfigureAwait(false) is not { } application)
            {
                return Result<SignatureView>.Failure(Error.NotFound("application.not_found", "You have no application with that id."));
            }

            if (application.Status is ApplicationStatuses.Withdrawn or ApplicationStatuses.Expired or ApplicationStatuses.Declined or ApplicationStatuses.Rejected)
            {
                return Result<SignatureView>.Failure(Error.Conflict("application.not_withdrawable", "This application has already ended."));
            }

            List<(WaiverRecord Waiver, bool IsRequired)> asked = await AskedAsync(application.ListingId, ct).ConfigureAwait(false);
            if (asked.Select(a => a.Waiver).FirstOrDefault(w => w.Id == waiverId) is not { } waiver)
            {
                return Result<SignatureView>.Failure(NotFound());
            }

            if (!string.Equals(hash, Hex(waiver.BodyHash), StringComparison.OrdinalIgnoreCase))
            {
                return Result<SignatureView>.Failure(Error.Conflict("waiver.changed", "The waiver's text isn't the text you were shown. Read it again."));
            }

            string? name = typedName?.Trim();
            if (name is not { Length: >= 2 and <= 100 } || name.Any(char.IsControl))
            {
                return Result<SignatureView>.Failure(Error.Validation("waiver.typed_name", "Type your full name to sign."));
            }

            FamilyMemberRecord member = await db.FamilyMembers.FirstAsync(m => m.Id == application.FamilyMemberId, ct).ConfigureAwait(false);
            string? dob = await cipher.DecryptAsync(tenant.TenantId, member.DateOfBirthEnc, ct).ConfigureAwait(false);
            bool minor = dob is null || Age.YearsOn(DateOnly.ParseExact(dob, "yyyy-MM-dd", CultureInfo.InvariantCulture), clock.TodayIn(tenant.TimeZone)) < 18;
            if (!minor)
            {
                return Result<SignatureView>.Failure(Error.Forbidden("waiver.adult_signs", "Someone 18 or over signs their own waivers."));
            }

            if (!ownerIsAdult)
            {
                return Result<SignatureView>.Failure(Error.Forbidden("waiver.guardian_signs", "A parent or guardian signs for someone under 18."));
            }

            if (await db.WaiverSignatures.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.WaiverId == waiver.Id && s.FamilyMemberId == member.Id && s.BodyHashSigned == waiver.BodyHash, ct).ConfigureAwait(false) is { } already)
            {
                return Result<SignatureView>.Success(View(already));
            }

            WaiverSignatureRecord signature = new()
            {
                Id = UuidV7.NewId(),
                TenantId = waiver.TenantId,
                WaiverId = waiver.Id,
                ApplicationId = application.Id,
                FamilyMemberId = member.Id,
                SignedByUserId = owner.Value,
                SignerRole = "guardian",
                TypedName = name,
                BodyHashSigned = waiver.BodyHash,
                SignedAt = clock.StoredNow(),
                IpHash = request.IpHash,
                UserAgentHash = request.UserAgentHash,
            };
            db.WaiverSignatures.Add(signature);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(SignedAction, application.Id, waiver.Id, ct).ConfigureAwait(false);
            return Result<SignatureView>.Success(View(signature));
        }, cancellationToken);

    /// <summary>The required waivers a young person hasn't signed for a listing, as it stands now.</summary>
    public async Task<IReadOnlyList<Guid>> UnsignedAsync(Guid listingId, Guid memberId, CancellationToken cancellationToken = default)
    {
        List<(WaiverRecord Waiver, bool IsRequired)> asked = await AskedAsync(listingId, cancellationToken).ConfigureAwait(false);
        List<WaiverSignatureRecord> signatures = await SignaturesAsync(memberId, asked.Select(a => a.Waiver.Id), cancellationToken).ConfigureAwait(false);
        return [.. asked.Where(a => a.IsRequired && !signatures.Any(s => s.WaiverId == a.Waiver.Id && s.BodyHashSigned.AsSpan().SequenceEqual(a.Waiver.BodyHash)))
            .Select(a => a.Waiver.Id)];
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>The text as stored and hashed: line endings as \n, no surrounding space.</summary>
    public static string? Body(string? body) =>
        body?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() is { Length: > 0 and <= MaxBodyLength } text
        && !text.Any(c => char.IsControl(c) && c is not '\n' and not '\t')
            ? text
            : null;

    public static byte[] Hash(string body) => SHA256.HashData(Encoding.UTF8.GetBytes(body));

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

    private async Task<List<(WaiverRecord Waiver, bool IsRequired)>> AskedAsync(Guid listingId, CancellationToken ct) =>
        [.. (await (from l in db.ListingWaivers.AsNoTracking()
                    join w in db.Waivers.AsNoTracking() on l.WaiverId equals w.Id
                    where l.ListingId == listingId && w.Status == Published
                    orderby w.Title
                    select new { w, l.IsRequired }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.w, x.IsRequired))];

    private Task<List<WaiverSignatureRecord>> SignaturesAsync(Guid memberId, IEnumerable<Guid> waivers, CancellationToken ct)
    {
        Guid[] ids = [.. waivers];
        return db.WaiverSignatures.AsNoTracking().Where(s => s.FamilyMemberId == memberId && ids.Contains(s.WaiverId)).ToListAsync(ct);
    }

    private Task<WaiverRecord?> OwnAsync(OrganisationId org, Guid id, CancellationToken ct) =>
        db.Waivers.FirstOrDefaultAsync(w => w.Id == id && w.OrgId == org.Value, ct);

    private async Task<ApplicationRecord?> OwnApplicationAsync(UserId owner, Guid id, CancellationToken ct) =>
        await (from a in db.Applications
               join p in db.FamilyProfiles on a.FamilyProfileId equals p.Id
               where a.Id == id && p.OwnerUserId == owner.Value
               select a).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<bool> OwnsListingAsync(OrganisationId org, Guid listingId, CancellationToken ct) =>
        (await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org;

    private async Task<WaiverView> ViewAsync(WaiverRecord waiver, CancellationToken ct)
    {
        List<WaiverListing> used = [.. (await db.ListingWaivers.AsNoTracking().Where(l => l.WaiverId == waiver.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(l => new WaiverListing(l.ListingId, l.IsRequired))];
        int signatures = await db.WaiverSignatures.CountAsync(s => s.WaiverId == waiver.Id, ct).ConfigureAwait(false);
        return new WaiverView(waiver.Id, waiver.Title, waiver.Version, waiver.Status, waiver.BodyMd, Hex(waiver.BodyHash), waiver.RequiresGuardian, waiver.CreatedAt, used, signatures);
    }

    private static SignatureView View(WaiverSignatureRecord s) => new(s.Id, s.WaiverId, s.SignerRole, s.TypedName, Hex(s.BodyHashSigned), s.SignedAt);

    private Task AuditAsync(string action, Guid application, Guid? waiver, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "application",
            SubjectIds = [application],
            Detail = waiver is { } w ? new Dictionary<string, string>(StringComparer.Ordinal) { ["waiver"] = w.ToString() } : new Dictionary<string, string>(),
        }, ct);

    private static Error BadBody() => Error.Validation("waiver.body", $"The waiver's text, up to {MaxBodyLength} characters.");

    private static Error NotFound() => Error.NotFound("waiver.not_found", "There's no such waiver here.");
}
