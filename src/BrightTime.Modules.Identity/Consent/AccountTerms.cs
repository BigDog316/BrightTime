using System.Security.Cryptography;
using System.Text;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Domain;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Identity.Consent;

/// <summary>A document a person agrees to, pinned by the hash of its exact words.</summary>
public sealed record AccountDocument(string Kind, string Version, string Text)
{
    public byte[] Hash { get; } = SHA256.HashData(Encoding.UTF8.GetBytes(Text));

    public string HashHex => Convert.ToHexStringLower(Hash);
}

/// <summary>Where a person stands with the current terms.</summary>
public sealed record TermsState(AccountDocument Current, DateTimeOffset? AcceptedAt);

/// <summary>
/// The terms of use (D37): one current version, which a person agrees to for themselves. A young
/// person's agreement is what lets them sign up for programs on their own
/// (<see cref="YouthCapabilityCalculator"/>'s <c>CanSelfEnroll</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each agreement is a <c>consent_record</c> naming the version and the hash of the words shown,
/// as a parental consent does (D31). A new version is agreed to afresh: capabilities read
/// agreement to the current hash, not to any version.
/// </para>
/// <para>
/// <b>The text is a draft</b> for counsel, and English only until it is reviewed and translated
/// (Q13), as the consent disclosures are.
/// </para>
/// </remarks>
public sealed class AccountTerms(IdentityDbContext db, Guardianship guardianship, AuditTrail audit, IAuditRequestInfo request, IClock clock)
{
    public const string Accepted = "account.terms_accepted";

    /// <summary>The current terms of use.</summary>
    public static AccountDocument Current { get; } = new(
        ConsentDocumentKind.Terms,
        "2026-10-20.draft",
        """
        BrightTime helps you find activities and sign up for them. These terms say what you can expect from us, and what we expect from you.

        What we do. We show you programs, events and jobs from organisations in your area. We check each organisation before its listings go up, but we don't run the activities. The organisation that runs an activity is responsible for it.

        What we keep. We keep your email address, your password (scrambled, so no one can read it), the month and year you were born, your interests, your area and what you save. We never keep your home address or where you are. We don't sell your information, show you ads, or track you on other sites.

        Your choices. You can change your interests and your area at any time. You can ask for a copy of your information, or ask for it to be deleted.

        Be safe and kind. Use your own account, and keep your password to yourself. Don't pretend to be someone else. Don't use BrightTime to bother, scare or hurt anyone. If something worries you, tell an adult you trust, or tell us.

        Signing up for programs. When you sign up for a program, the organisation that runs it gets the details it needs. Some programs have their own rules too.

        If you're under 18. A parent or guardian may manage some of what your account can do. If they do, they can see and change their consents for you.

        Changes. If we change these terms, we'll show you the new version, and ask you to agree again before you sign up for more programs.
        """);

    public Task<TermsState> StateAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct => new TermsState(Current, await AcceptedAtAsync(user.Value, ct).ConfigureAwait(false)), cancellationToken);

    /// <summary>
    /// Records the person's agreement to the current terms, naming the hash of the words they were
    /// shown; a stale hash is refused. Agreeing twice changes nothing.
    /// </summary>
    public Task<Result<TermsState>> AcceptAsync(UserId user, string? hash, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!string.Equals(hash, Current.HashHex, StringComparison.OrdinalIgnoreCase))
            {
                return Result<TermsState>.Failure(Error.Conflict("terms.stale", "The terms have changed. Show the current version.",
                    new Dictionary<string, object> { ["current"] = Current.HashHex }));
            }

            AppUser? account = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Value, ct).ConfigureAwait(false);
            if (account is not { Status: UserStatus.Active } || account.AccountKind == AccountKind.Child)
            {
                // A child profile has no sign-in of its own; its guardian consents for it (D31).
                return Result<TermsState>.Failure(Error.Forbidden("terms.not_for_this_account", "This account cannot agree to the terms itself."));
            }

            if (await AcceptedAtAsync(user.Value, ct).ConfigureAwait(false) is { } already)
            {
                return Result<TermsState>.Success(new TermsState(Current, already));
            }

            // To the microsecond, as the database keeps it, so this answer and the next read agree.
            DateTimeOffset now = clock.UtcNow;
            now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
            db.ConsentRecords.Add(new ConsentRecord
            {
                Id = UuidV7.NewId(),
                TenantId = account.TenantId,
                UserId = account.Id,
                DocumentKind = Current.Kind,
                DocumentVersion = Current.Version,
                DocumentHash = Current.Hash,
                AcceptedAt = now,
                IpHash = request.IpHash,
            });
            account.TermsAcceptedAt = now;
            account.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (account.AccountKind != AccountKind.Adult)
            {
                await guardianship.RecomputeCoreAsync(account.Id, ct).ConfigureAwait(false);
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Accepted,
                SubjectKind = "user",
                SubjectIds = [account.Id],
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = Current.Version },
            }, ct).ConfigureAwait(false);
            return Result<TermsState>.Success(new TermsState(Current, now));
        }, cancellationToken);

    /// <summary>When this person agreed to the current terms, or null.</summary>
    internal Task<DateTimeOffset?> AcceptedAtAsync(Guid user, CancellationToken ct)
    {
        byte[] hash = Current.Hash;
        return db.ConsentRecords.AsNoTracking()
            .Where(r => r.UserId == user && r.DocumentKind == ConsentDocumentKind.Terms && r.DocumentHash == hash)
            .OrderBy(r => r.AcceptedAt)
            .Select(r => (DateTimeOffset?)r.AcceptedAt)
            .FirstOrDefaultAsync(ct);
    }
}
