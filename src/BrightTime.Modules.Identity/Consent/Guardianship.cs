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

/// <summary>
/// The guardian's consent email (email plus, D31): to whom, about which child, and the
/// single-use token that answers it. The token is in the email only; the database holds its hash.
/// </summary>
public sealed record ConsentEmail(Guid LinkId, UserId ChildId, string GuardianEmail, string? GuardianLocale, string ChildGivenName, string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>Never the token or the address, so a logged value gives nothing away.</summary>
    public override string ToString() => $"ConsentEmail {{ LinkId = {LinkId} }}";
}

/// <summary>The confirmatory email after email-plus consent: the "plus".</summary>
public sealed record ConsentConfirmation(Guid LinkId, UserId ChildId, string GuardianEmail, string? GuardianLocale, string ChildGivenName)
{
    public override string ToString() => $"ConsentConfirmation {{ LinkId = {LinkId} }}";
}

/// <summary>A child profile just made, and the consent email its guardian is to be sent.</summary>
public sealed record NewChild(ChildView Child, ConsentEmail ConsentEmail);

/// <summary>A guardian's own open link, as the page that answers a consent email shows it.</summary>
public sealed record LinkToConfirm(Guid LinkId, UserId ChildId, string ChildGivenName, string Status);

/// <summary>A guardian's request that their child, now 13, have a sign-in of their own at this address (D35).</summary>
public sealed record ClaimAsk(UserId ChildId, string Email)
{
    /// <summary>Never the address: a logged request must not carry it.</summary>
    public override string ToString() => $"ClaimAsk {{ ChildId = {ChildId} }}";
}

/// <summary>
/// A child profile as its guardian sees it. <see cref="AccountKind"/> is <c>youth</c> once the young
/// person has claimed it (D35): it is theirs to sign in to, and still the guardian's to consent for.
/// </summary>
public sealed record ChildView(
    UserId Id,
    string GivenName,
    DateOnly BirthMonth,
    string AccountKind,
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
/// <para>
/// Email plus (16 CFR 312.5(b)(2)(vi)): making a profile issues a single-use token for the
/// guardian's consent email, kept as a SHA-256 hash on the link. The guardian answers it while
/// signed in as themselves (<see cref="VerifyByEmailAsync"/>), so a forwarded email is no use
/// to anyone else; the host then sends the confirmatory email. A signed form reviewed in person
/// is the fallback (<see cref="AttestSignedFormAsync"/>).
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

    /// <summary>Recorded by the host once a consent email has gone (or failed to).</summary>
    public const string ConsentEmailSent = "consent.email_sent";

    /// <summary>Recorded by the host once the confirmatory email has gone (or failed to).</summary>
    public const string ConfirmationSent = "consent.confirmation_sent";

    public const string ClaimRequested = "consent.claim_requested";

    /// <summary>Recorded by the host once a claim email has gone (or failed to).</summary>
    public const string ClaimEmailSent = "consent.claim_email_sent";

    /// <summary>Recorded when the young person completes a claim (<see cref="AccountClaims"/>).</summary>
    public const string AccountClaimed = "consent.account_claimed";

    /// <summary>
    /// How long a consent email's link works. Long enough for a parent to get to it after a
    /// few working days; the token is no use without the guardian's own sign-in.
    /// </summary>
    public static readonly TimeSpan EmailTokenLifetime = TimeSpan.FromDays(3);

    /// <summary>A new consent email at most this often, so the endpoint cannot be used to flood an inbox.</summary>
    public static readonly TimeSpan EmailResendInterval = TimeSpan.FromMinutes(1);

    private const int TokenBytes = 32;

    private TimeZoneInfo Zone => tenant.TimeZone;

    /// <summary>
    /// A guardian makes a child profile. It does nothing until the guardian's link is verified.
    /// Also issues the token for the consent email, which the caller sends.
    /// </summary>
    public Task<Result<NewChild>> CreateChildAsync(UserId guardian, string givenName, DateOnly birthMonth, string relationship, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string name = (givenName ?? string.Empty).Trim();
            if (name.Length is 0 or > MaxGivenNameLength || name.Any(char.IsControl))
            {
                return Result<NewChild>.Failure(Error.Validation("consent.invalid_given_name", $"A first name or nickname of 1 to {MaxGivenNameLength} characters."));
            }

            if (!GuardianRelationshipKind.All.Contains(relationship))
            {
                return Result<NewChild>.Failure(Error.Validation("consent.invalid_relationship", "One of " + string.Join(", ", GuardianRelationshipKind.All) + "."));
            }

            DateOnly today = clock.TodayIn(Zone);
            if (birthMonth.Day != 1 || birthMonth > today)
            {
                return Result<NewChild>.Failure(Error.Validation("consent.invalid_birth_month", "The first of a month, not in the future."));
            }

            AgeBand band = Age.BandFromBirthMonth(birthMonth, today);
            if (!band.IsCoppaProtected())
            {
                // 13 and over: a young person makes their own account and consents for themselves.
                return Result<NewChild>.Failure(Error.Validation("consent.not_under_13", "Child profiles are for children under 13."));
            }

            AppUser? adult = await db.Users.FirstOrDefaultAsync(u => u.Id == guardian.Value, ct).ConfigureAwait(false);
            if (adult is not { AccountKind: AccountKind.Adult, Status: UserStatus.Active })
            {
                return Result<NewChild>.Failure(Error.Forbidden("consent.not_an_adult_account", "Only an adult account can be a guardian."));
            }

            if (!adult.EmailConfirmed)
            {
                // Email plus starts from an address the guardian has shown they control.
                return Result<NewChild>.Failure(Error.Precondition("consent.email_unconfirmed", "Confirm your email address first."));
            }

            int existing = await db.GuardianLinks.CountAsync(l => l.GuardianUserId == guardian.Value && l.RevokedAt == null, ct).ConfigureAwait(false);
            if (existing >= MaxChildrenPerGuardian)
            {
                return Result<NewChild>.Failure(Error.Conflict("consent.too_many_children", "This account has as many child profiles as it can have."));
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
            string token = IssueEmailToken(link, now);
            db.GuardianLinks.Add(link);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            YouthCapabilities capabilities = await RecomputeCoreAsync(childId, ct).ConfigureAwait(false);
            await AuditAsync(ChildCreated, childId, new() { ["relationship"] = relationship }, ct).ConfigureAwait(false);

            return Result<NewChild>.Success(new NewChild(
                new ChildView(child.UserId, name, birthMonth, child.AccountKind, child.Status, View(link), [], capabilities),
                new ConsentEmail(link.Id, child.UserId, adult.Email!, adult.Locale, name, token, link.EmailTokenExpiresAt!.Value)));
        }, cancellationToken);

    /// <summary>
    /// A fresh consent email for a pending link: the guardian lost the first, or let it run out.
    /// The new token replaces the old, which stops working.
    /// </summary>
    public Task<Result<ConsentEmail>> ResendConsentEmailAsync(UserId guardian, Guid linkId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await LockOwnLinkAsync(guardian, linkId, ct).ConfigureAwait(false);
            if (link is null)
            {
                return Result<ConsentEmail>.Failure(LinkNotFound());
            }

            if (link.Status == GuardianLinkStatus.Verified)
            {
                return Result<ConsentEmail>.Failure(AlreadyVerified());
            }

            if (link.Status != GuardianLinkStatus.Pending)
            {
                return Result<ConsentEmail>.Failure(RevokedLinkError());
            }

            DateTimeOffset now = clock.UtcNow;
            if (link.EmailTokenIssuedAt is { } issued && now - issued < EmailResendInterval)
            {
                return Result<ConsentEmail>.Failure(new Error(ErrorKind.RateLimited, "consent.email_recently_sent",
                    "A consent email was sent a moment ago. Wait a minute before asking again."));
            }

            AppUser? adult = await ActiveGuardianAsync(guardian, ct).ConfigureAwait(false);
            if (adult is null)
            {
                return Result<ConsentEmail>.Failure(NotAnAdult());
            }

            string token = IssueEmailToken(link, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            string name = await GivenNameAsync(link.ChildUserId, ct).ConfigureAwait(false);
            return Result<ConsentEmail>.Success(new ConsentEmail(
                link.Id, new UserId(link.ChildUserId), adult.Email!, adult.Locale, name, token, link.EmailTokenExpiresAt!.Value));
        }, cancellationToken);

    /// <summary>
    /// The guardian answers their consent email (email plus): verifies the link, records the
    /// <c>account</c> consent to the disclosure the email carried, and activates the profile.
    /// The caller sends the confirmatory email this returns.
    /// </summary>
    /// <remarks>
    /// The caller must be the link's guardian, signed in: anyone else holding the email gets
    /// nothing from it. A wrong, replaced or spent token reads the same; only the right token
    /// says it has run out. The link is locked for the check, so a double tap verifies once.
    /// </remarks>
    public Task<Result<ConsentConfirmation>> VerifyByEmailAsync(UserId guardian, Guid linkId, string? token, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await LockOwnLinkAsync(guardian, linkId, ct).ConfigureAwait(false);
            if (link is null)
            {
                return Result<ConsentConfirmation>.Failure(LinkNotFound());
            }

            if (link.Status == GuardianLinkStatus.Verified)
            {
                return Result<ConsentConfirmation>.Failure(AlreadyVerified());
            }

            if (link.Status != GuardianLinkStatus.Pending)
            {
                return Result<ConsentConfirmation>.Failure(RevokedLinkError());
            }

            if (link.EmailTokenHash is not { } stored || !TryDecodeToken(token, out byte[] presented)
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented), stored))
            {
                return Result<ConsentConfirmation>.Failure(Error.Forbidden("consent.token_invalid",
                    "That consent link is not valid. Use the newest email, or ask for another."));
            }

            if (clock.UtcNow >= link.EmailTokenExpiresAt)
            {
                return Result<ConsentConfirmation>.Failure(Error.Precondition("consent.token_expired",
                    "That consent link has run out. Ask for a new email."));
            }

            // Email plus rests on an address the guardian still controls.
            AppUser? adult = await ActiveGuardianAsync(guardian, ct).ConfigureAwait(false);
            if (adult is null)
            {
                return Result<ConsentConfirmation>.Failure(NotAnAdult());
            }

            await VerifyCoreAsync(link, VerificationMethod.EmailPlus, null, ct).ConfigureAwait(false);
            string name = await GivenNameAsync(link.ChildUserId, ct).ConfigureAwait(false);
            return Result<ConsentConfirmation>.Success(new ConsentConfirmation(
                link.Id, new UserId(link.ChildUserId), adult.Email!, adult.Locale, name));
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

            if (!disclosure.AvailableUnder13 && await IsUnder13Async(child.Value, ct).ConfigureAwait(false))
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
            ClearEmailToken(link);

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

    /// <summary>
    /// The guardian's own open link, for the web page a consent email's link opens (D35); null when
    /// it is not theirs or was revoked, so a link opened by the wrong account says nothing about it.
    /// </summary>
    public Task<LinkToConfirm?> LinkToConfirmAsync(UserId guardian, Guid linkId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<LinkToConfirm?>(async ct =>
        {
            GuardianLink? link = await db.GuardianLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == linkId && l.GuardianUserId == guardian.Value && l.RevokedAt == null, ct).ConfigureAwait(false);
            return link is null
                ? null
                : new LinkToConfirm(link.Id, new UserId(link.ChildUserId), await GivenNameAsync(link.ChildUserId, ct).ConfigureAwait(false), link.Status);
        }, cancellationToken);

    /// <summary>One child, if the guardian has an open link to them.</summary>
    public Task<ChildView?> ChildAsync(UserId guardian, UserId child, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            GuardianLink? link = await db.GuardianLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.GuardianUserId == guardian.Value && l.ChildUserId == child.Value && l.RevokedAt == null, ct).ConfigureAwait(false);
            return link is null ? null : await ViewChildAsync(link, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// A guardian asks that their child, now 13, have a sign-in of their own, at an address the young
    /// person reads (D35). Checks who and when, and records the request; the host then sends the
    /// claim email after the response, so the answer never says whether the address has an account.
    /// </summary>
    public Task<Result<ClaimAsk>> RequestClaimAsync(UserId guardian, UserId child, string? email, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string address = (email ?? string.Empty).Trim();
            if (address.Length is 0 or > SignIn.AccountProvisioning.MaxEmailLength
                || !System.Net.Mail.MailAddress.TryCreate(address, out System.Net.Mail.MailAddress? parsed)
                || !string.Equals(parsed.Address, address, StringComparison.Ordinal))
            {
                return Result<ClaimAsk>.Failure(Error.Validation("consent.invalid_email", "The young person's own email address."));
            }

            if (await VerifiedLinkAsync(guardian, child, ct).ConfigureAwait(false) is null)
            {
                return Result<ClaimAsk>.Failure(NotYourChild());
            }

            AppUser profile = await db.Users.AsNoTracking().FirstAsync(u => u.Id == child.Value, ct).ConfigureAwait(false);
            if (profile.AccountKind != AccountKind.Child)
            {
                return Result<ClaimAsk>.Failure(Error.Conflict("consent.already_claimed", "This account already has a sign-in of its own."));
            }

            if (profile.Status != UserStatus.Active)
            {
                return Result<ClaimAsk>.Failure(Error.Precondition("consent.claim_needs_consent", "The profile needs your consent before it can be handed over."));
            }

            // The same younger reading as everywhere: never a day early.
            if (profile.BirthMonth is not { } month || Age.BandFromBirthMonth(month, clock.TodayIn(Zone)).IsCoppaProtected())
            {
                return Result<ClaimAsk>.Failure(Error.Forbidden("consent.claim_too_young", "A child can have a sign-in of their own from the day they turn 13."));
            }

            await AuditAsync(ClaimRequested, child.Value, [], ct).ConfigureAwait(false);
            return Result<ClaimAsk>.Success(new ClaimAsk(child, address));
        }, cancellationToken);

    /// <summary>
    /// Books a recompute of one account's capabilities, to run at once on the worker. For changes
    /// made where no one is signed in to recompute as (a registration, a claim): the actor guard
    /// keeps <c>youth_capability</c> from an anonymous request, and the worker is exempt from it.
    /// The reason is part of the job's key, so each cause books once: <c>registered</c>, <c>claimed</c>.
    /// </summary>
    public Task RequestRecomputeAsync(UserId user, string reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            await db.EnqueueJobAsync(new JobRequest
            {
                JobType = RecomputeCapabilitiesHandler.JobType,
                TenantId = tenant.TenantId,
                Payload = new RecomputeCapabilitiesHandler.Payload(user.Value),
                UniqueKey = $"{RecomputeCapabilitiesHandler.JobType}:{user.Value:N}:{reason}",
            }, ct).ConfigureAwait(false);
            return 0;
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

        // Spent, whichever way the link was verified.
        ClearEmailToken(link);

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

    /// <summary>Mints a token for the consent email and keeps its hash on the link, replacing any earlier one.</summary>
    private static string IssueEmailToken(GuardianLink link, DateTimeOffset now)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(TokenBytes);
        link.EmailTokenHash = SHA256.HashData(secret);
        link.EmailTokenIssuedAt = now;
        link.EmailTokenExpiresAt = now + EmailTokenLifetime;
        return Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void ClearEmailToken(GuardianLink link)
    {
        link.EmailTokenHash = null;
        link.EmailTokenIssuedAt = null;
        link.EmailTokenExpiresAt = null;
    }

    private static bool TryDecodeToken(string? token, out byte[] secret)
    {
        secret = [];
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return false;
        }

        string base64 = token.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        byte[] buffer = new byte[48];
        if (!Convert.TryFromBase64String(base64, buffer, out int written) || written != TokenBytes)
        {
            return false;
        }

        secret = buffer[..written];
        return true;
    }

    /// <summary>The guardian's own open link, locked for the rest of the transaction.</summary>
    private async Task<GuardianLink?> LockOwnLinkAsync(UserId guardian, Guid linkId, CancellationToken ct)
    {
        List<GuardianLink> locked = await db.GuardianLinks
            .FromSql($"select * from identity.guardian_link where id = {linkId} for update")
            .ToListAsync(ct).ConfigureAwait(false);
        return locked is [{ } link] && link.GuardianUserId == guardian.Value && link.RevokedAt is null ? link : null;
    }

    private Task<AppUser?> ActiveGuardianAsync(UserId guardian, CancellationToken ct) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(
            u => u.Id == guardian.Value && u.AccountKind == AccountKind.Adult && u.Status == UserStatus.Active && u.EmailConfirmed && u.Email != null, ct);

    private async Task<string> GivenNameAsync(Guid child, CancellationToken ct) =>
        await db.UserClaims.AsNoTracking()
            .Where(c => c.UserId == child && c.ClaimType == AppUserClaimType.GivenName)
            .Select(c => c.ClaimValue)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? string.Empty;

    private Task<GuardianLink?> VerifiedLinkAsync(UserId guardian, UserId child, CancellationToken ct) =>
        db.GuardianLinks.FirstOrDefaultAsync(
            l => l.GuardianUserId == guardian.Value && l.ChildUserId == child.Value
                 && l.Status == GuardianLinkStatus.Verified && l.RevokedAt == null, ct);

    /// <summary>A child profile keeps the floor until it is claimed; an account with no birth month is treated as under 13.</summary>
    private async Task<bool> IsUnder13Async(Guid userId, CancellationToken ct)
    {
        AppUser user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        return user.AccountKind == AccountKind.Child
               || user.BirthMonth is not { } month
               || Age.BandFromBirthMonth(month, clock.TodayIn(Zone)).IsCoppaProtected();
    }

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
            child.AccountKind,
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

    private static Error LinkNotFound() =>
        Error.NotFound("consent.link_not_found", "You have no open link with that id.");

    private static Error RevokedLinkError() =>
        Error.Conflict("consent.link_revoked", "This link was revoked; the guardian makes a new one.");

    private static Error AlreadyVerified() =>
        Error.Conflict("consent.already_verified", "This link is already verified.");

    private static Error NotAnAdult() =>
        Error.Forbidden("consent.not_an_adult_account", "Only an adult account can be a guardian.");

    private static Error NotYourChild() =>
        Error.Forbidden("consent.not_guardian", "You are not this child's verified guardian.");
}
