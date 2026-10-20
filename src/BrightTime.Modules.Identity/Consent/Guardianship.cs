using System.Security.Cryptography;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Domain;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Identity.Consent;

public sealed record GuardianLinkView(Guid Id, string Relationship, string Status, string? VerificationMethod, DateTimeOffset? VerifiedAt);

public sealed record ConsentView(ConsentScope Scope, string Method, DateTimeOffset GrantedAt);

/// <summary>A child profile as its guardian sees it.</summary>
public sealed record ChildView(
    UserId Id,
    string GivenName,
    DateOnly BirthMonth,
    string Status,
    GuardianLinkView Link,
    IReadOnlyList<ConsentView> Consents,
    YouthCapabilities Capabilities);

/// <summary>
/// Guardians, child profiles and parental consent: the write side of COPPA (docs/plan/05,
/// "Identity &amp; consent").
/// </summary>
/// <remarks>
/// <para>
/// Every change runs in one transaction with its audit row and a recomputation of the child's
/// <c>youth_capability</c>, so no read can see consent changed but capabilities not. Revoking
/// takes effect on the child's next request: principals are rebuilt from the database each
/// time (D28).
/// </para>
/// <para>
/// Who may act is decided here, record by record (D4). Row-level security keeps the tenant and
/// requires an actor; it does not know who is whose guardian.
/// </para>
/// <para>
/// A child profile is an under-13 account with no password of its own. It is
/// <c>pending_consent</c> until its guardian's link is verified, which records the
/// <c>account</c> consent and activates it: the schema refuses an active child account without
/// that row (<c>identity.enforce_child_consent</c>).
/// </para>
/// </remarks>
public sealed class Guardianship(IdentityDbContext db, ITenantContext tenant, AuditTrail audit, IAuditRequestInfo request, IClock clock)
{
    public const int MaxGivenNameLength = 50;
    public const int MaxChildrenPerGuardian = 12;

    public const string ChildCreated = "consent.child_created";
    public const string LinkVerified = "consent.link_verified";
    public const string LinkRevoked = "consent.link_revoked";
    public const string ConsentGranted = "consent.granted";
    public const string ConsentRevoked = "consent.revoked";
    public const string Turned13 = "consent.turned_13";

    private TimeZoneInfo Zone => tenant.TimeZone;

    /// <summary>A guardian makes a child profile. It does nothing until the guardian's link is verified.</summary>
    public Task<Result<ChildView>> CreateChildAsync(UserId guardian, string givenName, DateOnly birthMonth, string relationship, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string name = (givenName ?? string.Empty).Trim();
            if (name.Length is 0 or > MaxGivenNameLength || name.Any(char.IsControl))
            {
                return Result<ChildView>.Failure(Error.Validation("consent.invalid_given_name", $"A first name or nickname of 1 to {MaxGivenNameLength} characters."));
            }

            if (!GuardianRelationshipKind.All.Contains(relationship))
            {
                return Result<ChildView>.Failure(Error.Validation("consent.invalid_relationship", "One of " + string.Join(", ", GuardianRelationshipKind.All) + "."));
            }

            DateOnly today = clock.TodayIn(Zone);
            if (birthMonth.Day != 1 || birthMonth > today)
            {
                return Result<ChildView>.Failure(Error.Validation("consent.invalid_birth_month", "The first of a month, not in the future."));
            }

            AgeBand band = Age.BandFromBirthMonth(birthMonth, today);
            if (!band.IsCoppaProtected())
            {
                // 13 and over: a young person makes their own account and consents for themselves.
                return Result<ChildView>.Failure(Error.Validation("consent.not_under_13", "Child profiles are for children under 13."));
            }

            AppUser? adult = await db.Users.FirstOrDefaultAsync(u => u.Id == guardian.Value, ct).ConfigureAwait(false);
            if (adult is not { AccountKind: AccountKind.Adult, Status: UserStatus.Active })
            {
                return Result<ChildView>.Failure(Error.Forbidden("consent.not_an_adult_account", "Only an adult account can be a guardian."));
            }

            if (!adult.EmailConfirmed)
            {
                // Email plus starts from an address the guardian has shown they control.
                return Result<ChildView>.Failure(Error.Precondition("consent.email_unconfirmed", "Confirm your email address first."));
            }

            int existing = await db.GuardianLinks.CountAsync(l => l.GuardianUserId == guardian.Value && l.RevokedAt == null, ct).ConfigureAwait(false);
            if (existing >= MaxChildrenPerGuardian)
            {
                return Result<ChildView>.Failure(Error.Conflict("consent.too_many_children", "This account has as many child profiles as it can have."));
            }

            DateTimeOffset now = clock.UtcNow;
            Guid childId = UuidV7.NewId();
            string userName = "child-" + childId.ToString("N");
            AppUser child = new()
            {
                Id = childId,
                TenantId = adult.TenantId,
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                SecurityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                AccountKind = AccountKind.Child,
                Status = UserStatus.PendingConsent,
                Locale = adult.Locale,
                BirthMonth = birthMonth,
                AgeBand = band.ToDbValue(),
                CreatedAt = now,
                UpdatedAt = now,
            };

            GuardianLink link = new()
            {
                Id = UuidV7.NewId(),
                TenantId = adult.TenantId,
                GuardianUserId = guardian.Value,
                ChildUserId = childId,
                Relationship = relationship,
                Status = GuardianLinkStatus.Pending,
                IsPrimary = true,
                CreatedAt = now,
            };

            db.Users.Add(child);
            // Saved first: the claim and the link reference the account, and this context's model
            // has no relationships for EF to order the inserts by.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.UserClaims.Add(new AppUserClaim { UserId = childId, ClaimType = AppUserClaimType.GivenName, ClaimValue = name });
            db.GuardianLinks.Add(link);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            YouthCapabilities capabilities = await RecomputeCoreAsync(childId, ct).ConfigureAwait(false);
            await AuditAsync(ChildCreated, childId, new() { ["relationship"] = relationship }, ct).ConfigureAwait(false);

            return Result<ChildView>.Success(new ChildView(child.UserId, name, birthMonth, child.Status, View(link), [], capabilities));
        }, cancellationToken);

    /// <summary>
    /// An organiser has reviewed a signed consent form in person (Q3's fallback). Verifies the
    /// link and records the guardian's <c>account</c> consent, which activates the profile.
    /// </summary>
    /// <remarks>The caller holds <c>consent.attest_signed_form</c>; the endpoint checks it.</remarks>
    public Task<Result<GuardianLinkView>> AttestSignedFormAsync(Guid linkId, UserId organiser, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await db.GuardianLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct).ConfigureAwait(false);
            if (link is null)
            {
                return Result<GuardianLinkView>.Failure(Error.NotFound("consent.link_not_found", "No guardian link has that id."));
            }

            if (link.RevokedAt is not null || link.Status is GuardianLinkStatus.Revoked or GuardianLinkStatus.Rejected)
            {
                return Result<GuardianLinkView>.Failure(Error.Conflict("consent.link_revoked", "This link was revoked; the guardian makes a new one."));
            }

            if (link.GuardianUserId == organiser.Value)
            {
                return Result<GuardianLinkView>.Failure(Error.Forbidden("consent.own_link", "Nobody attests their own guardianship."));
            }

            await VerifyCoreAsync(link, VerificationMethod.SignedFormReview, organiser, ct).ConfigureAwait(false);
            return Result<GuardianLinkView>.Success(View(link));
        }, cancellationToken);

    /// <summary>A verified guardian consents to one scope, having been shown the text <paramref name="disclosureHash"/> names.</summary>
    public Task<Result<ConsentView>> GrantAsync(UserId guardian, UserId child, ConsentScope scope, string? disclosureHash, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await VerifiedLinkAsync(guardian, child, ct).ConfigureAwait(false);
            if (link is null)
            {
                return Result<ConsentView>.Failure(NotYourChild());
            }

            ConsentDisclosure disclosure = ConsentDisclosures.For(scope);
            if (!string.Equals(disclosureHash, disclosure.HashHex, StringComparison.OrdinalIgnoreCase))
            {
                return Result<ConsentView>.Failure(Error.Conflict("consent.stale_disclosure", "The wording has changed. Show the guardian the current text.",
                    new Dictionary<string, object> { ["current"] = disclosure.HashHex }));
            }

            if (!disclosure.AvailableUnder13)
            {
                return Result<ConsentView>.Failure(Error.Forbidden("consent.not_available_under_13",
                    "Children under 13 never post or publish, whatever their guardian consents to."));
            }

            if (!disclosure.Methods.Contains(link.VerificationMethod ?? string.Empty))
            {
                return Result<ConsentView>.Failure(Error.Forbidden("consent.method_insufficient",
                    "This consent discloses your child's information, so it needs a signed form reviewed in person.",
                    new Dictionary<string, object> { ["required"] = VerificationMethod.SignedFormReview }));
            }

            string value = scope.ToDbValue();
            DateTimeOffset now = clock.UtcNow;
            ParentalConsent? live = await db.ParentalConsents.FirstOrDefaultAsync(
                c => c.ChildUserId == child.Value && c.Scope == value && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now), ct).ConfigureAwait(false);
            if (live is not null)
            {
                return Result<ConsentView>.Success(new ConsentView(scope, live.Method, live.GrantedAt));
            }

            ParentalConsent consent = Record(link, scope, now);
            db.ParentalConsents.Add(consent);
            if (scope == ConsentScope.Account)
            {
                await SetStatusAsync(child.Value, UserStatus.Active, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await RecomputeCoreAsync(child.Value, ct).ConfigureAwait(false);
            await AuditAsync(ConsentGranted, child.Value, new() { ["scope"] = value, ["method"] = consent.Method }, ct).ConfigureAwait(false);
            return Result<ConsentView>.Success(new ConsentView(scope, consent.Method, consent.GrantedAt));
        }, cancellationToken);

    /// <summary>
    /// A guardian withdraws one scope. Withdrawing <c>account</c> stops the profile working
    /// until it is given again.
    /// </summary>
    public Task<Result> RevokeAsync(UserId guardian, UserId child, ConsentScope scope, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await VerifiedLinkAsync(guardian, child, ct).ConfigureAwait(false) is null)
            {
                return Result.Failure(NotYourChild());
            }

            string value = scope.ToDbValue();
            List<ParentalConsent> live = await db.ParentalConsents
                .Where(c => c.ChildUserId == child.Value && c.Scope == value && c.RevokedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            if (live.Count == 0)
            {
                return Result.Failure(Error.NotFound("consent.no_consent", "There is no consent for that to withdraw."));
            }

            DateTimeOffset now = clock.UtcNow;
            foreach (ParentalConsent c in live)
            {
                c.RevokedAt = now;
                c.RevokedReason = "withdrawn_by_guardian";
            }

            if (scope == ConsentScope.Account)
            {
                await SetStatusAsync(child.Value, UserStatus.PendingConsent, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await RecomputeCoreAsync(child.Value, ct).ConfigureAwait(false);
            await AuditAsync(ConsentRevoked, child.Value, new() { ["scope"] = value }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>
    /// A guardian ends their own link. Every consent given through it ends too, and a profile
    /// left with no account consent stops working.
    /// </summary>
    public Task<Result> RevokeLinkAsync(UserId guardian, Guid linkId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await db.GuardianLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.GuardianUserId == guardian.Value, ct).ConfigureAwait(false);
            if (link is null || link.RevokedAt is not null)
            {
                return Result.Failure(Error.NotFound("consent.link_not_found", "You have no open link with that id."));
            }

            DateTimeOffset now = clock.UtcNow;
            link.Status = GuardianLinkStatus.Revoked;
            link.RevokedAt = now;
            link.RevokedReason = "revoked_by_guardian";
            // ck_guardian_verified: verified_at is set exactly while the link is verified. When
            // and how it was verified stays on record in the audit log (consent.link_verified).
            link.VerifiedAt = null;

            List<ParentalConsent> given = await db.ParentalConsents
                .Where(c => c.GuardianLinkId == link.Id && c.RevokedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (ParentalConsent c in given)
            {
                c.RevokedAt = now;
                c.RevokedReason = "guardian_link_revoked";
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (!await HasLiveAccountConsentAsync(link.ChildUserId, now, ct).ConfigureAwait(false))
            {
                await SetStatusAsync(link.ChildUserId, UserStatus.PendingConsent, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await RecomputeCoreAsync(link.ChildUserId, ct).ConfigureAwait(false);
            await AuditAsync(LinkRevoked, link.ChildUserId, new() { ["consents_ended"] = given.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The guardian's children: every profile they have an open link to, verified or not.</summary>
    public Task<IReadOnlyList<ChildView>> ChildrenAsync(UserId guardian, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ChildView>>(async ct =>
        {
            List<GuardianLink> links = await db.GuardianLinks.AsNoTracking()
                .Where(l => l.GuardianUserId == guardian.Value && l.RevokedAt == null)
                .OrderBy(l => l.CreatedAt)
                .ToListAsync(ct).ConfigureAwait(false);

            List<ChildView> children = [];
            foreach (GuardianLink link in links)
            {
                children.Add(await ViewChildAsync(link, ct).ConfigureAwait(false));
            }

            return children;
        }, cancellationToken);

    /// <summary>One child, if the guardian has an open link to them.</summary>
    public Task<ChildView?> ChildAsync(UserId guardian, UserId child, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await db.GuardianLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.GuardianUserId == guardian.Value && l.ChildUserId == child.Value && l.RevokedAt == null, ct).ConfigureAwait(false);
            return link is null ? null : await ViewChildAsync(link, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Verifies a link by a method, records the account consent and activates the profile.</summary>
    internal async Task VerifyCoreAsync(GuardianLink link, string method, UserId? verifiedBy, CancellationToken ct)
    {
        DateTimeOffset now = clock.UtcNow;
        if (link.Status != GuardianLinkStatus.Verified)
        {
            link.Status = GuardianLinkStatus.Verified;
            link.VerificationMethod = method;
            link.VerifiedAt = now;
            link.VerifiedBy = verifiedBy?.Value;
        }

        if (!await HasLiveAccountConsentAsync(link.ChildUserId, now, ct).ConfigureAwait(false))
        {
            db.ParentalConsents.Add(Record(link, ConsentScope.Account, now));
        }

        await SetStatusAsync(link.ChildUserId, UserStatus.Active, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RecomputeCoreAsync(link.ChildUserId, ct).ConfigureAwait(false);
        await AuditAsync(LinkVerified, link.ChildUserId, new() { ["method"] = method }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Recomputes and stores a child's capabilities from age, status, guardianship and consent.
    /// Runs inside the caller's transaction, after its changes are saved.
    /// </summary>
    internal async Task<YouthCapabilities> RecomputeCoreAsync(Guid userId, CancellationToken ct)
    {
        DateTimeOffset now = clock.UtcNow;
        AppUser user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        bool verifiedGuardian = await db.GuardianLinks.AsNoTracking()
            .AnyAsync(l => l.ChildUserId == userId && l.Status == GuardianLinkStatus.Verified && l.RevokedAt == null, ct).ConfigureAwait(false);
        List<string> scopes = await db.ParentalConsents.AsNoTracking()
            .Where(c => c.ChildUserId == userId && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now))
            .Select(c => c.Scope)
            .ToListAsync(ct).ConfigureAwait(false);

        AgeBand band = user.BirthMonth is { } month ? Age.BandFromBirthMonth(month, clock.TodayIn(Zone)) : AgeBand.Unknown;
        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(new CapabilityInputs
        {
            UserId = user.UserId,
            AccountKind = user.AccountKind,
            Status = user.Status,
            AgeBand = band,
            HasVerifiedGuardian = verifiedGuardian,
            LiveConsents = scopes.Select(s => ConsentScopeExtensions.TryFromDbValue(s, out ConsentScope c) ? (ConsentScope?)c : null)
                .OfType<ConsentScope>().ToHashSet(),
            PhoneConfirmed = user.PhoneConfirmed,
            TermsAccepted = user.TermsAcceptedAt is not null,
        });

        YouthCapabilityRecord? record = await db.YouthCapabilities.FirstOrDefaultAsync(c => c.UserId == userId, ct).ConfigureAwait(false);
        bool turned13 = AgeBandExtensions.FromDbValue(user.AgeBand).IsCoppaProtected() && !band.IsCoppaProtected();
        if (record is null)
        {
            record = new YouthCapabilityRecord { UserId = userId, TenantId = user.TenantId };
            db.YouthCapabilities.Add(record);
        }

        YouthCapabilityCalculator.ApplyTo(record, capabilities, now);

        // The stored band follows the birth month, so age-band search and eligibility see it too.
        if (user.BirthMonth is not null && user.AgeBand != band.ToDbValue())
        {
            AppUser tracked = await db.Users.FirstAsync(u => u.Id == userId, ct).ConfigureAwait(false);
            tracked.AgeBand = band.ToDbValue();
            tracked.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await ScheduleNextRecomputeAsync(user, ct).ConfigureAwait(false);

        // A child profile keeps the under-13 floor after 13 (the calculator reads its kind):
        // protections hold until the young person has a sign-in of their own. The day is on record.
        if (turned13 && user.BirthMonth is not null)
        {
            await AuditAsync(Turned13, userId, new() { ["age_band"] = band.ToDbValue() }, ct).ConfigureAwait(false);
        }

        return capabilities;
    }

    /// <summary>
    /// Recomputes one account's capabilities: for the job that runs when its age band changes,
    /// and for anything else that changes what the account may do.
    /// </summary>
    public Task<YouthCapabilities> RecomputeAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => RecomputeCoreAsync(user.Value, ct), cancellationToken);

    /// <summary>
    /// Capabilities depend on age, which changes without anyone doing anything. Rather than scan
    /// every account each night, each recompute books the next one for the day the band changes,
    /// in the same transaction, keyed so a repeat books nothing twice.
    /// </summary>
    private async Task ScheduleNextRecomputeAsync(AppUser user, CancellationToken ct)
    {
        if (user.AccountKind == AccountKind.Adult || user.BirthMonth is not { } month
            || Age.NextBandChange(month, clock.TodayIn(Zone)) is not { } day)
        {
            return;
        }

        // Local midnight in the tenant's zone, the moment the younger reading says the band moves.
        DateTimeOffset runAfter = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Zone);
        await db.EnqueueJobAsync(new JobRequest
        {
            JobType = RecomputeCapabilitiesHandler.JobType,
            TenantId = new TenantId(user.TenantId),
            Payload = new RecomputeCapabilitiesHandler.Payload(user.Id),
            UniqueKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{RecomputeCapabilitiesHandler.JobType}:{user.Id:N}:{day:yyyy-MM-dd}"),
            RunAfter = runAfter,
        }, ct).ConfigureAwait(false);
    }

    private ParentalConsent Record(GuardianLink link, ConsentScope scope, DateTimeOffset now) => new()
    {
        Id = UuidV7.NewId(),
        TenantId = link.TenantId,
        ChildUserId = link.ChildUserId,
        GuardianUserId = link.GuardianUserId,
        GuardianLinkId = link.Id,
        Scope = scope.ToDbValue(),
        // How the guardian was verified is how sure we are it was them who consented.
        Method = link.VerificationMethod ?? VerificationMethod.EmailPlus,
        GrantedAt = now,
        ConsentTextHash = ConsentDisclosures.For(scope).Hash,
        IpHash = request.IpHash,
        UserAgentHash = request.UserAgentHash,
    };

    private Task<GuardianLink?> VerifiedLinkAsync(UserId guardian, UserId child, CancellationToken ct) =>
        db.GuardianLinks.FirstOrDefaultAsync(
            l => l.GuardianUserId == guardian.Value && l.ChildUserId == child.Value
                 && l.Status == GuardianLinkStatus.Verified && l.RevokedAt == null, ct);

    private Task<bool> HasLiveAccountConsentAsync(Guid child, DateTimeOffset now, CancellationToken ct)
    {
        string account = ConsentScope.Account.ToDbValue();
        return db.ParentalConsents.AnyAsync(
            c => c.ChildUserId == child && c.Scope == account && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now), ct);
    }

    private async Task SetStatusAsync(Guid userId, string status, CancellationToken ct)
    {
        AppUser user = await db.Users.FirstAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        user.Status = status;
        user.UpdatedAt = clock.UtcNow;
    }

    private async Task<ChildView> ViewChildAsync(GuardianLink link, CancellationToken ct)
    {
        DateTimeOffset now = clock.UtcNow;
        AppUser child = await db.Users.AsNoTracking().FirstAsync(u => u.Id == link.ChildUserId, ct).ConfigureAwait(false);
        string name = await db.UserClaims.AsNoTracking()
            .Where(c => c.UserId == child.Id && c.ClaimType == AppUserClaimType.GivenName)
            .Select(c => c.ClaimValue)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? string.Empty;
        List<ParentalConsent> consents = await db.ParentalConsents.AsNoTracking()
            .Where(c => c.ChildUserId == child.Id && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now))
            .OrderBy(c => c.GrantedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        YouthCapabilityRecord? record = await db.YouthCapabilities.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == child.Id, ct).ConfigureAwait(false);

        return new ChildView(
            child.UserId,
            name,
            child.BirthMonth ?? default,
            child.Status,
            View(link),
            [.. consents.Select(c => ConsentScopeExtensions.TryFromDbValue(c.Scope, out ConsentScope s) ? new ConsentView(s, c.Method, c.GrantedAt) : null).OfType<ConsentView>()],
            record is null ? YouthCapabilities.Denied(child.UserId) : YouthCapabilityCalculator.FromRecord(record));
    }

    private Task AuditAsync(string action, Guid child, Dictionary<string, string> detail, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "user",
            SubjectIds = [child],
            Detail = detail,
        }, ct);

    private static GuardianLinkView View(GuardianLink l) => new(l.Id, l.Relationship, l.Status, l.VerificationMethod, l.VerifiedAt);

    private static Error NotYourChild() =>
        Error.Forbidden("consent.not_guardian", "You are not this child's verified guardian.");
}
