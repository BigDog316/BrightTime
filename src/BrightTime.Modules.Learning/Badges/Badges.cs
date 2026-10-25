using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Badges;

public sealed record BadgeClassInput(string? Name, string? Description, string? Criteria, short? ExpiresAfterMonths);

public sealed record BadgeClassView(Guid Id, string Slug, string Name, string Description, string Criteria, short? ExpiresAfterMonths, string Status, int Awarded);

/// <summary>A badge as the organisation that awarded it sees it: who (as their share names them), when, and whether it stands.</summary>
[YouthData]
public sealed record AwardView(Guid Id, string? RecipientName, string VerifySlug, DateTimeOffset IssuedOn, DateTimeOffset? ExpiresOn, DateTimeOffset? RevokedAt, string? RevocationReason);

/// <summary>A badge as the young person who holds it sees it, with the credential to take anywhere.</summary>
[YouthData]
public sealed record HeldBadge(Guid Id, string Name, string Issuer, DateTimeOffset IssuedOn, DateTimeOffset? ExpiresOn, bool Revoked, Uri Verify, string Credential);

/// <summary>What the public verification page says about a credential. Never who holds it.</summary>
public sealed record Verification(string Status, string Name, string Issuer, DateTimeOffset IssuedOn, DateTimeOffset? ExpiresOn, DateTimeOffset? RevokedAt, string? RevocationReason, JsonElement Credential, string Jwt);

/// <summary>
/// Open Badges (M4, D66): an organisation's achievements, awarded to the young people who took part
/// with it, as an OB 3.0 credential signed RS256 and an OB 2.0 hosted assertion; revoked with a
/// reason; and verified by anyone without signing in.
/// </summary>
/// <remarks>
/// <para>
/// The organisation is the issuer, under its own <c>did:web</c> DID. Its signing key lives in the
/// vault (<c>badge-issuer-{org}</c>); only the public half and the reference are stored. Each use
/// checks the vault's key against the active one, and a different key retires the old and becomes
/// active: rotation in the vault is picked up, and every key ever used stays in the DID document,
/// so older credentials still verify.
/// </para>
/// <para>
/// A badge goes only to a young person with their own account who is enrolled with the
/// organisation, and once: a second award of the same badge waits until the first is revoked.
/// </para>
/// </remarks>
public sealed partial class Badges(
    LearningDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IKeyVault vault,
    IDirectoryQueries directory,
    IEnrolmentQueries enrolments)
{
    public const int MaxName = 200;
    public const int MaxDescription = 2000;
    public const int MaxCriteria = 10000;
    public const int MaxNarrative = 2000;

    public const string Defined = "learning.badge.defined";
    public const string Issued = "learning.badge.issued";
    public const string Revoked = "learning.badge.revoked";
    public const string Read = "learning.badge.read";

    public const string Draft = "draft";
    public const string Published = "published";
    public const string Retired = "retired";

    // ---- the organisation's badges -------------------------------------------------------------

    public Task<IReadOnlyList<BadgeClassView>> ListAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<BadgeClassView>>(async ct =>
        {
            List<BadgeClassRecord> classes = await (from c in db.BadgeClasses.AsNoTracking()
                                                    join i in db.Issuers on c.IssuerId equals i.Id
                                                    where i.OrgId == org.Value
                                                    orderby c.Name
                                                    select c).ToListAsync(ct).ConfigureAwait(false);
            Guid[] ids = [.. classes.Select(c => c.Id)];
            Dictionary<Guid, int> awarded = await db.BadgeAssertions.AsNoTracking().Where(a => ids.Contains(a.BadgeClassId) && a.RevokedAt == null)
                .GroupBy(a => a.BadgeClassId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
            return [.. classes.Select(c => View(c, awarded.GetValueOrDefault(c.Id)))];
        }, cancellationToken);

    /// <summary>A new badge, as a draft. The organisation becomes an issuer the first time.</summary>
    public Task<Result<BadgeClassView>> CreateAsync(OrganisationId org, Uri site, BadgeClassInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (Check(input) is { } invalid)
            {
                return Result<BadgeClassView>.Failure(invalid);
            }

            IssuerProfileRecord issuer = await IssuerAsync(org, site, ct).ConfigureAwait(false);
            string stem = Slugged(input.Name!);
            List<string> taken = await db.BadgeClasses.AsNoTracking().Where(c => c.TenantId == tenant.TenantId.Value && c.Slug.StartsWith(stem))
                .Select(c => c.Slug).ToListAsync(ct).ConfigureAwait(false);
            string slug = stem;
            for (int n = 2; taken.Contains(slug, StringComparer.OrdinalIgnoreCase); n++)
            {
                slug = $"{stem}-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            BadgeClassRecord badge = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                IssuerId = issuer.Id,
                Slug = slug,
                Name = input.Name!.Trim(),
                Description = input.Description!.Trim(),
                CriteriaMd = input.Criteria!.Trim(),
                Version = 1,
                ExpiresAfterMonths = input.ExpiresAfterMonths,
                Status = Draft,
                CreatedAt = clock.StoredNow(),
            };
            db.BadgeClasses.Add(badge);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [badge.Id], ct, "created").ConfigureAwait(false);
            return Result<BadgeClassView>.Success(View(badge, 0));
        }, cancellationToken);

    /// <summary>Changes a draft. A published badge never changes: what was awarded is what it says.</summary>
    public Task<Result<BadgeClassView>> UpdateAsync(OrganisationId org, Guid id, BadgeClassInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } badge)
            {
                return Result<BadgeClassView>.Failure(NotFound());
            }

            if (badge.Status != Draft)
            {
                return Result<BadgeClassView>.Failure(Error.Conflict("badges.not_draft", NotDraft));
            }

            if (Check(input) is { } invalid)
            {
                return Result<BadgeClassView>.Failure(invalid);
            }

            badge.Name = input.Name!.Trim();
            badge.Description = input.Description!.Trim();
            badge.CriteriaMd = input.Criteria!.Trim();
            badge.ExpiresAfterMonths = input.ExpiresAfterMonths;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [badge.Id], ct, "changed").ConfigureAwait(false);
            return Result<BadgeClassView>.Success(View(badge, 0));
        }, cancellationToken);

    public Task<Result<BadgeClassView>> PublishAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        MoveAsync(org, id, Draft, Published, "badges.not_draft", NotDraft, cancellationToken);

    /// <summary>Stops a badge being awarded. Badges already awarded stand.</summary>
    public Task<Result<BadgeClassView>> RetireAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        MoveAsync(org, id, Published, Retired, "badges.not_published", NotPublished, cancellationToken);

    // ---- awarding --------------------------------------------------------------------------------

    /// <summary>Awards a published badge to the young person behind an enrolment, signing the credential.</summary>
    public Task<Result<AwardView>> IssueAsync(OrganisationId org, UserId staff, Uri site, Guid badgeClassId, Guid enrollmentId, string? narrative, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            if (await OwnAsync(org, badgeClassId, ct).ConfigureAwait(false) is not { } badge)
            {
                return Result<AwardView>.Failure(NotFound());
            }

            if (badge.Status != Published)
            {
                return Result<AwardView>.Failure(Error.Conflict("badges.not_published", NotPublished));
            }

            string? story = narrative?.Trim() is { Length: > 0 } n ? n : null;
            if (story is { Length: > MaxNarrative })
            {
                return Result<AwardView>.Failure(Error.Validation("badges.narrative", "Keep the note to 2,000 characters."));
            }

            IReadOnlyList<EnrolmentFacts> found = await enrolments.FactsAsync([enrollmentId], DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct).ConfigureAwait(false);
            if (found.Count == 0 || found[0].OrgId != org.Value || found[0].YouthUserId is not { } recipient)
            {
                return Result<AwardView>.Failure(Error.Validation("badges.recipient",
                    "Choose a young person enrolled with your organisation whose family has linked them to their own account."));
            }

            if (await db.BadgeAssertions.AnyAsync(a => a.BadgeClassId == badge.Id && a.RecipientUserId == recipient && a.RevokedAt == null, ct).ConfigureAwait(false))
            {
                return Result<AwardView>.Failure(Error.Conflict("badges.already_awarded", "This young person already holds this badge."));
            }

            BadgeAssertionRecord award = await SignAsync(org, site, badge, recipient, story, staff.Value, enrollmentId, null, ct).ConfigureAwait(false);
            return Result<AwardView>.Success(new AwardView(award.Id, null, award.VerifySlug, award.IssuedOn, award.ExpiresOn, null, null));
        }, cancellationToken);

    /// <summary>
    /// The completion badge of a playlist a young person has just completed (D67), awarded by the
    /// playlist's organisation. Null when they already hold it, or the badge can't be awarded.
    /// </summary>
    internal async Task<BadgeAssertionRecord?> AwardForPlaylistAsync(OrganisationId org, Uri site, Guid badgeClassId, Guid recipient, Guid playlistEnrollmentId, string playlistTitle, CancellationToken ct)
    {
        if (await OwnAsync(org, badgeClassId, ct).ConfigureAwait(false) is not { Status: Published } badge
            || await db.BadgeAssertions.AnyAsync(a => a.BadgeClassId == badge.Id && a.RecipientUserId == recipient && a.RevokedAt == null, ct).ConfigureAwait(false))
        {
            return null;
        }

        return await SignAsync(org, site, badge, recipient, $"Completed the playlist “{playlistTitle}”.", null, null, playlistEnrollmentId, ct).ConfigureAwait(false);
    }

    /// <summary>Revokes an award, with the reason; verifiers are told at once, and the young person keeps the record.</summary>
    public Task<Result<AwardView>> RevokeAsync(OrganisationId org, Guid assertionId, string? reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? why = reason?.Trim();
            if (why is not { Length: > 0 and <= 500 })
            {
                return Result<AwardView>.Failure(Error.Validation("badges.reason", "Say why, in up to 500 characters."));
            }

            BadgeAssertionRecord? award = await (from a in db.BadgeAssertions
                                                 join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                                                 join i in db.Issuers on c.IssuerId equals i.Id
                                                 where a.Id == assertionId && i.OrgId == org.Value
                                                 select a).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (award is null)
            {
                return Result<AwardView>.Failure(Error.NotFound("badges.award_not_found", "Your organisation awarded no badge like that."));
            }

            if (award.RevokedAt is not null)
            {
                return Result<AwardView>.Failure(Error.Conflict("badges.already_revoked", "This badge is already revoked."));
            }

            award.RevokedAt = clock.StoredNow();
            award.RevocationReason = why;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Revoked, org, [award.Id], ct, why, award.RecipientUserId).ConfigureAwait(false);
            return Result<AwardView>.Success(new AwardView(award.Id, null, award.VerifySlug, award.IssuedOn, award.ExpiresOn, award.RevokedAt, award.RevocationReason));
        }, cancellationToken);

    /// <summary>A badge's awards, newest first, each named as the young person's family shares them with the organisation.</summary>
    public Task<Result<IReadOnlyList<AwardView>>> AwardsAsync(OrganisationId org, Guid badgeClassId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, badgeClassId, ct).ConfigureAwait(false) is null)
            {
                return Result<IReadOnlyList<AwardView>>.Failure(NotFound());
            }

            List<BadgeAssertionRecord> awards = await db.BadgeAssertions.AsNoTracking().Where(a => a.BadgeClassId == badgeClassId)
                .OrderByDescending(a => a.IssuedOn).Take(500).ToListAsync(ct).ConfigureAwait(false);
            Dictionary<Guid, string?> names = await NamesAsync([.. awards.Where(a => a.EnrollmentId != null).Select(a => a.EnrollmentId!.Value).Distinct()], ct).ConfigureAwait(false);
            await AuditAsync(Read, org, [.. awards.Select(a => a.Id)], ct).ConfigureAwait(false);
            return Result<IReadOnlyList<AwardView>>.Success([.. awards.Select(a =>
                new AwardView(a.Id, a.EnrollmentId is { } e ? names.GetValueOrDefault(e) : null, a.VerifySlug, a.IssuedOn, a.ExpiresOn, a.RevokedAt, a.RevocationReason))]);
        }, cancellationToken);

    /// <summary>The badges a young person holds, revoked ones included and marked.</summary>
    public Task<IReadOnlyList<HeldBadge>> HeldAsync(UserId user, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<HeldBadge>>(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            var held = await (from a in db.BadgeAssertions.AsNoTracking()
                              join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                              join i in db.Issuers on c.IssuerId equals i.Id
                              where a.RecipientUserId == user.Value
                              orderby a.IssuedOn descending
                              select new { a, c.Name, Issuer = i.Name }).ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "badge_assertion", SubjectIds = [.. held.Select(h => h.a.Id)] }, ct).ConfigureAwait(false);
            return [.. held.Select(h => new HeldBadge(h.a.Id, h.Name, h.Issuer, h.a.IssuedOn, h.a.ExpiresOn, h.a.RevokedAt is not null,
                BadgeDocuments.Credential(site, h.a.VerifySlug), h.a.CredentialJwt ?? string.Empty))];
        }, cancellationToken);

    // ---- anyone, without signing in -------------------------------------------------------------

    /// <summary>A credential's standing, by its verification name: valid, expired or revoked.</summary>
    public Task<Verification?> VerifyAsync(string slug, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<Verification?>(async ct =>
        {
            var found = await (from a in db.BadgeAssertions.AsNoTracking()
                               join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                               join i in db.Issuers on c.IssuerId equals i.Id
                               where a.VerifySlug == slug
                               select new { a, c.Name, Issuer = i.Name }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (found?.a.CredentialJson is null || found.a.CredentialJwt is null)
            {
                return null;
            }

            string status = found.a.RevokedAt is not null ? "revoked" : found.a.ExpiresOn is { } expires && expires <= clock.UtcNow ? "expired" : "valid";
            using JsonDocument credential = JsonDocument.Parse(found.a.CredentialJson);
            return new Verification(status, found.Name, found.Issuer, found.a.IssuedOn, found.a.ExpiresOn, found.a.RevokedAt, found.a.RevocationReason,
                credential.RootElement.Clone(), found.a.CredentialJwt);
        }, cancellationToken);

    /// <summary>The OB 2.0 hosted assertion, or the revocation that replaces it; null when there is no such badge.</summary>
    public Task<(JsonObject Document, bool Revoked)?> Assertion2Async(string slug, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<(JsonObject, bool)?>(async ct =>
        {
            var found = await (from a in db.BadgeAssertions.AsNoTracking()
                               join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                               join i in db.Issuers on c.IssuerId equals i.Id
                               where a.VerifySlug == slug
                               select new { a, c, i }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (found is null)
            {
                return null;
            }

            if (found.a.RevokedAt is not null)
            {
                return (BadgeDocuments.Revoked2(site, slug, found.a.RevocationReason ?? string.Empty), true);
            }

            BadgeFacts facts = new(site, found.i.OrgId ?? Guid.Empty, found.i.Did, found.i.Name, found.c.Id, found.c.Name, found.c.Description, found.c.CriteriaMd,
                slug, found.a.IssuedOn, found.a.ExpiresOn, BadgeDocuments.RecipientIdentity(found.a.RecipientUserId), Encoding.UTF8.GetString(found.a.RecipientSalt), found.a.Narrative);
            return (BadgeDocuments.Assertion2(facts), false);
        }, cancellationToken);

    /// <summary>A badge as OB 2.0 (BadgeClass) or OB 3.0 (Achievement) describes it; drafts aren't public.</summary>
    public Task<JsonObject?> BadgeClassAsync(Guid id, Uri site, bool openBadges2, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<JsonObject?>(async ct =>
        {
            var found = await (from c in db.BadgeClasses.AsNoTracking()
                               join i in db.Issuers on c.IssuerId equals i.Id
                               where c.Id == id && c.Status != Draft
                               select new { c, i }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (found is null)
            {
                return null;
            }

            Guid org = found.i.OrgId ?? Guid.Empty;
            if (openBadges2)
            {
                return BadgeDocuments.BadgeClass2(site, org, found.c.Id, found.c.Name, found.c.Description, found.c.CriteriaMd);
            }

            JsonObject achievement = BadgeDocuments.Achievement(new BadgeFacts(site, org, found.i.Did, found.i.Name, found.c.Id, found.c.Name, found.c.Description,
                found.c.CriteriaMd, string.Empty, default, null, string.Empty, string.Empty, null));
            achievement["@context"] = new JsonArray(BadgeDocuments.CredentialsContext, BadgeDocuments.OpenBadgesContext);
            return achievement;
        }, cancellationToken);

    public Task<string?> ImageAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<string?>(async ct =>
        {
            var found = await (from c in db.BadgeClasses.AsNoTracking()
                               join i in db.Issuers on c.IssuerId equals i.Id
                               where c.Id == id && c.Status != Draft
                               select new { c.Name, Issuer = i.Name }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return found is null ? null : BadgeDocuments.ImageSvg(found.Name, found.Issuer);
        }, cancellationToken);

    /// <summary>The issuer's DID document, every key it has signed with and not withdrawn as compromised.</summary>
    public Task<JsonObject?> DidDocumentAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<JsonObject?>(async ct =>
        {
            // The county's own issuer (D68) sits under the tenant's id, where an organisation's sits under its own.
            IssuerProfileRecord? issuer = orgId == tenant.TenantId.Value
                ? await db.Issuers.AsNoTracking().FirstOrDefaultAsync(i => i.OrgId == null, ct).ConfigureAwait(false)
                : await db.Issuers.AsNoTracking().FirstOrDefaultAsync(i => i.OrgId == orgId, ct).ConfigureAwait(false);
            if (issuer is null)
            {
                return null;
            }

            List<IssuerKeyRecord> keys = await db.IssuerKeys.AsNoTracking().Where(k => k.IssuerId == issuer.Id && k.Status != "compromised")
                .OrderByDescending(k => k.ActivatedAt).ToListAsync(ct).ConfigureAwait(false);
            return BadgeDocuments.DidDocumentFor(issuer.Did, keys.Select(k => (k.KeyId, k.PublicJwk)));
        }, cancellationToken);

    public Task<JsonObject?> Issuer2Async(Guid orgId, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<JsonObject?>(async ct =>
            await db.Issuers.AsNoTracking().FirstOrDefaultAsync(i => i.OrgId == orgId, ct).ConfigureAwait(false) is { } issuer
                ? BadgeDocuments.Issuer2Document(site, orgId, issuer.Name)
                : null, cancellationToken);

    /// <summary>The issuer's revoked credentials, as a 1EdTechRevocationList.</summary>
    public Task<JsonObject?> RevocationsAsync(Guid orgId, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<JsonObject?>(async ct =>
        {
            if (await db.Issuers.AsNoTracking().FirstOrDefaultAsync(i => i.OrgId == orgId, ct).ConfigureAwait(false) is not { } issuer)
            {
                return null;
            }

            var revoked = await (from a in db.BadgeAssertions.AsNoTracking()
                                 join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                                 where c.IssuerId == issuer.Id && a.RevokedAt != null
                                 orderby a.RevokedAt
                                 select new { a.VerifySlug, a.RevocationReason }).ToListAsync(ct).ConfigureAwait(false);
            JsonArray list = [];
            foreach (var r in revoked)
            {
                list.Add(new JsonObject { ["id"] = BadgeDocuments.Credential(site, r.VerifySlug).ToString(), ["revocationReason"] = r.RevocationReason });
            }

            return new JsonObject { ["id"] = BadgeDocuments.Revocations(site, orgId).ToString(), ["issuer"] = issuer.Did, ["revokedCredentials"] = list };
        }, cancellationToken);

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Signs a badge for a recipient as the organisation, and stores the credential.</summary>
    private async Task<BadgeAssertionRecord> SignAsync(
        OrganisationId org, Uri site, BadgeClassRecord badge, Guid recipient, string? story, Guid? awardedBy, Guid? enrollmentId, Guid? playlistEnrollmentId, CancellationToken ct)
    {
        IssuerProfileRecord issuer = await db.Issuers.FirstAsync(i => i.Id == badge.IssuerId, ct).ConfigureAwait(false);
        IssuerKeyRecord key = await ActiveKeyAsync(issuer, ct).ConfigureAwait(false);
        DateTimeOffset now = clock.StoredNow();
        string salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string identity = BadgeDocuments.RecipientIdentity(recipient);
        BadgeFacts facts = new(site, org.Value, issuer.Did, issuer.Name, badge.Id, badge.Name, badge.Description, badge.CriteriaMd,
            VerifySlug(), now, badge.ExpiresAfterMonths is { } months ? now.AddMonths(months) : null, identity, salt, story);
        JsonObject credential = BadgeDocuments.Credential(facts);
        string signingInput = BadgeDocuments.SigningInput(credential, $"{issuer.Did}#{key.KeyId}", key.PublicJwk, facts.IssuedOn, facts.ExpiresOn);
        byte[] signature = await vault.SignAsync(key.PrivateKeyRef, Encoding.UTF8.GetBytes(signingInput), ct).ConfigureAwait(false);

        BadgeAssertionRecord award = new()
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            BadgeClassId = badge.Id,
            RecipientUserId = recipient,
            RecipientIdentityHash = BadgeDocuments.IdentityHash(identity, salt),
            RecipientSalt = Encoding.UTF8.GetBytes(salt),
            IssuedOn = now,
            ExpiresOn = facts.ExpiresOn,
            Narrative = story,
            IssuerKeyId = key.Id,
            CredentialJwt = BadgeDocuments.Jwt(signingInput, signature),
            CredentialJson = credential.ToJsonString(),
            VerifySlug = facts.VerifySlug,
            AwardedBy = awardedBy,
            EnrollmentId = enrollmentId,
            SourcePlaylistEnrollmentId = playlistEnrollmentId,
        };
        db.BadgeAssertions.Add(award);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(Issued, org, [award.Id], ct, playlistEnrollmentId is null ? null : "playlist", recipient).ConfigureAwait(false);
        return award;
    }

    /// <summary>The organisation's issuer profile, made the first time it defines a badge.</summary>
    private async Task<IssuerProfileRecord> IssuerAsync(OrganisationId org, Uri site, CancellationToken ct)
    {
        if (await db.Issuers.FirstOrDefaultAsync(i => i.OrgId == org.Value, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        IssuerProfileRecord issuer = new()
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            OrgId = org.Value,
            Did = BadgeDocuments.Did(site, org.Value),
            Name = await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? string.Empty,
            Url = site.ToString(),
            Status = "active",
            CreatedAt = clock.StoredNow(),
        };
        db.Issuers.Add(issuer);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return issuer;
    }

    /// <summary>
    /// Signs a credential the county's site issues itself (D68): a CLR vouching for a young person's
    /// record as a whole. The site's issuer has no organisation, sits under the tenant's id, and is
    /// made the first time it signs.
    /// </summary>
    internal async Task<string> SignAsSiteAsync(Uri site, string siteName, DateTimeOffset issuedOn, Func<string, JsonObject> credentialFor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(credentialFor);
        if (await db.Issuers.FirstOrDefaultAsync(i => i.OrgId == null, ct).ConfigureAwait(false) is not { } issuer)
        {
            issuer = new IssuerProfileRecord
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                Did = BadgeDocuments.Did(site, tenant.TenantId.Value),
                Name = siteName,
                Url = site.ToString(),
                Status = "active",
                CreatedAt = clock.StoredNow(),
            };
            db.Issuers.Add(issuer);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        IssuerKeyRecord key = await ActiveKeyAsync(issuer, ct).ConfigureAwait(false);
        JsonObject credential = credentialFor(issuer.Did);
        string signingInput = BadgeDocuments.SigningInput(credential, $"{issuer.Did}#{key.KeyId}", key.PublicJwk, issuedOn, null);
        byte[] signature = await vault.SignAsync(key.PrivateKeyRef, Encoding.UTF8.GetBytes(signingInput), ct).ConfigureAwait(false);
        return BadgeDocuments.Jwt(signingInput, signature);
    }

    /// <summary>
    /// The issuer's signing key as the vault holds it now. A key the vault has replaced is retired,
    /// not removed, so credentials it signed still verify.
    /// </summary>
    private async Task<IssuerKeyRecord> ActiveKeyAsync(IssuerProfileRecord issuer, CancellationToken ct)
    {
        string reference = issuer.OrgId is { } org ? $"badge-issuer-{org:N}" : $"clr-issuer-{issuer.TenantId:N}";
        string jwk = await vault.GetPublicJwkAsync(reference, ct).ConfigureAwait(false);
        string keyId = BadgeDocuments.Thumbprint(jwk);
        IssuerKeyRecord? active = await db.IssuerKeys.FirstOrDefaultAsync(k => k.IssuerId == issuer.Id && k.Status == "active", ct).ConfigureAwait(false);
        if (active?.KeyId == keyId)
        {
            return active;
        }

        DateTimeOffset now = clock.StoredNow();
        if (active is not null)
        {
            active.Status = "retired";
            active.RetiredAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (await db.IssuerKeys.FirstOrDefaultAsync(k => k.IssuerId == issuer.Id && k.KeyId == keyId, ct).ConfigureAwait(false) is { } known)
        {
            known.Status = "active";
            known.RetiredAt = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return known;
        }

        IssuerKeyRecord key = new()
        {
            Id = UuidV7.NewId(),
            IssuerId = issuer.Id,
            KeyId = keyId,
            Algorithm = "RS256",
            PublicJwk = jwk,
            PrivateKeyRef = reference,
            Status = "active",
            ActivatedAt = now,
        };
        db.IssuerKeys.Add(key);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return key;
    }

    private Task<Result<BadgeClassView>> MoveAsync(OrganisationId org, Guid id, string from, string to, string code, string message, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } badge)
            {
                return Result<BadgeClassView>.Failure(NotFound());
            }

            if (badge.Status != from)
            {
                return Result<BadgeClassView>.Failure(Error.Conflict(code, message));
            }

            badge.Status = to;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [badge.Id], ct, to).ConfigureAwait(false);
            int awarded = await db.BadgeAssertions.CountAsync(a => a.BadgeClassId == badge.Id && a.RevokedAt == null, ct).ConfigureAwait(false);
            return Result<BadgeClassView>.Success(View(badge, awarded));
        }, cancellationToken);

    private Task<BadgeClassRecord?> OwnAsync(OrganisationId org, Guid id, CancellationToken ct) =>
        (from c in db.BadgeClasses
         join i in db.Issuers on c.IssuerId equals i.Id
         where c.Id == id && i.OrgId == org.Value
         select c).FirstOrDefaultAsync(ct);

    /// <summary>Each enrolment's young person as the family's share with the organisation names them; absent where it doesn't.</summary>
    private async Task<Dictionary<Guid, string?>> NamesAsync(IReadOnlyCollection<Guid> enrollmentIds, CancellationToken ct)
    {
        Dictionary<Guid, string?> names = [];
        if (enrollmentIds.Count == 0)
        {
            return names;
        }

        IReadOnlyList<EnrolmentSummary> found = await enrolments.GetAsync(enrollmentIds, ct).ConfigureAwait(false);
        foreach (Guid listing in found.Select(e => e.ListingId).Distinct())
        {
            foreach (RosterEntry entry in await enrolments.RosterAsync(listing, ct).ConfigureAwait(false))
            {
                if (enrollmentIds.Contains(entry.EnrollmentId))
                {
                    names[entry.EnrollmentId] = entry.DisplayName;
                }
            }
        }

        return names;
    }

    private static Error? Check(BadgeClassInput input)
    {
        if (input.Name?.Trim() is not { Length: > 0 and <= MaxName })
        {
            return Error.Validation("badges.name", "Write a name of up to 200 characters.");
        }

        if (input.Description?.Trim() is not { Length: > 0 and <= MaxDescription })
        {
            return Error.Validation("badges.description", "Write a description of up to 2,000 characters.");
        }

        if (input.Criteria?.Trim() is not { Length: > 20 and <= MaxCriteria })
        {
            return Error.Validation("badges.criteria", "Write criteria of more than 20 characters: what someone did to earn it.");
        }

        return input.ExpiresAfterMonths is < 1 or > 120
            ? Error.Validation("badges.expiry", "Choose an expiry of 1 to 120 months, or none.")
            : null;
    }

    private static string Slugged(string name)
    {
        string slug = NotSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "badge" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    /// <summary>A verification name: 128 random bits, base32, lower case. Names nobody.</summary>
    private static string VerifySlug()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        StringBuilder slug = new(26);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                slug.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            slug.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return slug.ToString();
    }

    private static BadgeClassView View(BadgeClassRecord c, int awarded) =>
        new(c.Id, c.Slug, c.Name, c.Description, c.CriteriaMd, c.ExpiresAfterMonths, c.Status, awarded);

    private Task AuditAsync(string action, OrganisationId org, IReadOnlyCollection<Guid> subjects, CancellationToken ct, string? detail = null, Guid? recipient = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = action == Defined ? "badge_class" : "badge_assertion",
            SubjectIds = recipient is { } r ? [.. subjects, r] : subjects,
            ActorOrgId = org,
            Detail = detail is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = detail },
        }, ct);

    private const string NotDraft = "Only a draft badge can be changed or published. Retire a published one and make another.";
    private const string NotPublished = "Only a published badge can be awarded or retired.";

    private static Error NotFound() => Error.NotFound("badges.not_found", "There's no badge like that.");

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
