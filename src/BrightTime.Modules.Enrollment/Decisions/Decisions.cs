using System.Globalization;
using System.Text.Json;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Eligibility;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Decisions;

public sealed record SeatView(int Capacity, int SeatsTaken, int? WaitlistCapacity, int WaitlistTaken);

/// <summary>
/// An application as its organisation sees it. The applicant's name and date of birth come only
/// through the family's live share (D44); without one they are null.
/// </summary>
[YouthData]
public sealed record OrgApplicationSummary(Guid Id, Guid ListingId, string? FirstName, string? LastName, string Status, DateTimeOffset? SubmittedAt, bool? IsEligible, int? WaitlistPosition);

[YouthData]
public sealed record OrgApplicationView(
    Guid Id,
    Guid ListingId,
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth,
    string Status,
    DateTimeOffset? SubmittedAt,
    EligibilityResult Eligibility,
    IReadOnlyDictionary<string, JsonElement> Answers,
    int? WaitlistPosition,
    DateTimeOffset? DecidedAt,
    string? DecisionNote);

/// <summary>One application's outcome in a bulk decision: its new status, or why it was left alone.</summary>
public sealed record BulkOutcome(Guid Id, string? Status, string? Error);

/// <summary>
/// An organisation's side of applications (M3a, D48): its listings' seats, the queue of
/// submitted applications, and the decisions that move them: review, accept (taking a seat),
/// waitlist, decline, and offering freed seats to the waitlist.
/// </summary>
/// <remarks>
/// <para>
/// The caller has checked the actor's permission for the organisation. Every method here finds
/// only applications to that organisation's listings, and never a draft: a family's draft is
/// theirs until they submit it.
/// </para>
/// <para>
/// Every read is one <c>enrollment.application.org_read</c> row and every decision one
/// <c>enrollment.application.decided</c> row, both with the organisation as the actor's.
/// </para>
/// </remarks>
public sealed class Decisions(
    EnrollmentDbContext db,
    IFieldCipher cipher,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IListingDetails listings,
    SealedAnswers sealedAnswers,
    SeatLedger seats)
{
    public const string OrgRead = "enrollment.application.org_read";
    public const string Decided = "enrollment.application.decided";

    public const string Accept = "accept";
    public const string Waitlist = "waitlist";
    public const string Decline = "decline";
    public const string Review = "review";

    public const int MaxNoteLength = 1000;
    public const int MaxBulk = 100;

    private static readonly string[] Visible =
        [ApplicationStatuses.Submitted, ApplicationStatuses.UnderReview, ApplicationStatuses.Waitlisted, ApplicationStatuses.Offered,
         ApplicationStatuses.Accepted, ApplicationStatuses.Declined, ApplicationStatuses.Withdrawn];

    // ---- seats -------------------------------------------------------------------------------

    public Task<Result<SeatView?>> SeatsAsync(OrganisationId org, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<SeatView?>.Failure(NoListing());
            }

            SeatPoolRecord? pool = await db.SeatPools.AsNoTracking().FirstOrDefaultAsync(p => p.ListingId == listingId && p.Label == SeatLedger.DefaultLabel, ct).ConfigureAwait(false);
            return Result<SeatView?>.Success(pool is null ? null : View(pool));
        }, cancellationToken);

    public Task<Result<SeatView>> SetSeatsAsync(OrganisationId org, Guid listingId, int capacity, int? waitlistCapacity, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<SeatView>.Failure(NoListing());
            }

            Result<SeatPoolRecord> set = await seats.SetAsync(listingId, capacity, waitlistCapacity, ct).ConfigureAwait(false);
            return set.IsSuccess ? Result<SeatView>.Success(View(set.Value)) : Result<SeatView>.Failure(set.Error);
        }, cancellationToken);

    // ---- reading -----------------------------------------------------------------------------

    /// <summary>A listing's submitted applications, oldest first (first come, first considered), optionally of one status.</summary>
    public Task<Result<IReadOnlyList<OrgApplicationSummary>>> QueueAsync(OrganisationId org, Guid listingId, string? status, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<IReadOnlyList<OrgApplicationSummary>>.Failure(NoListing());
            }

            List<ApplicationRecord> queue = await db.Applications.AsNoTracking()
                .Where(a => a.ListingId == listingId && Visible.Contains(a.Status) && (status == null || a.Status == status))
                .OrderBy(a => a.SubmittedAt).Take(500).ToListAsync(ct).ConfigureAwait(false);
            List<OrgApplicationSummary> summaries = [];
            foreach (ApplicationRecord a in queue)
            {
                (string? first, string? last, _) = await SharedAsync(org, a, ct).ConfigureAwait(false);
                summaries.Add(new OrgApplicationSummary(a.Id, a.ListingId, first, last, a.Status, a.SubmittedAt, a.IsEligible, await PositionAsync(a, ct).ConfigureAwait(false)));
            }

            await AuditAsync(OrgRead, org, [.. queue.Select(a => a.Id)], null, ct).ConfigureAwait(false);
            return Result<IReadOnlyList<OrgApplicationSummary>>.Success(summaries);
        }, cancellationToken);

    public Task<Result<OrgApplicationView>> GetAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Result<OrgApplicationView>.Failure(NotFound());
            }

            OrgApplicationView view = await ViewAsync(org, application, ct).ConfigureAwait(false);
            await AuditAsync(OrgRead, org, [application.Id], null, ct).ConfigureAwait(false);
            return Result<OrgApplicationView>.Success(view);
        }, cancellationToken);

    // ---- deciding ----------------------------------------------------------------------------

    /// <summary>
    /// Decides one application. Accepting takes a seat (or fails when there's none);
    /// waitlisting takes the next place; declining gives back a waitlist place; reviewing only
    /// marks it as being looked at.
    /// </summary>
    public Task<Result<OrgApplicationView>> DecideAsync(OrganisationId org, UserId actor, Guid id, string decision, string? note, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Result<OrgApplicationView>.Failure(NotFound());
            }

            Result decided = await ApplyAsync(org, actor, application, decision, note, ct).ConfigureAwait(false);
            return decided.IsSuccess
                ? Result<OrgApplicationView>.Success(await ViewAsync(org, application, ct).ConfigureAwait(false))
                : Result<OrgApplicationView>.Failure(decided.Error);
        }, cancellationToken);

    /// <summary>The same decision for many applications, each on its own merits: one failing leaves the others decided.</summary>
    public Task<Result<IReadOnlyList<BulkOutcome>>> BulkDecideAsync(OrganisationId org, UserId actor, IReadOnlyList<Guid> ids, string decision, string? note, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count is 0 or > MaxBulk)
            {
                return Result<IReadOnlyList<BulkOutcome>>.Failure(Error.Validation("decision.bulk", $"1 to {MaxBulk} applications."));
            }

            List<BulkOutcome> outcomes = [];
            foreach (Guid id in ids.Distinct())
            {
                if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } application)
                {
                    outcomes.Add(new BulkOutcome(id, null, "decision.not_found"));
                    continue;
                }

                Result decided = await ApplyAsync(org, actor, application, decision, note, ct).ConfigureAwait(false);
                outcomes.Add(decided.IsSuccess ? new BulkOutcome(id, application.Status, null) : new BulkOutcome(id, null, decided.Error.Code));
            }

            return Result<IReadOnlyList<BulkOutcome>>.Success(outcomes);
        }, cancellationToken);

    /// <summary>Offers freed seats to the front of a listing's waitlist. Returns the applications offered one.</summary>
    public Task<Result<IReadOnlyList<Guid>>> OfferAsync(OrganisationId org, UserId actor, Guid listingId, int count, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<IReadOnlyList<Guid>>.Failure(NoListing());
            }

            if (count is < 1 or > MaxBulk)
            {
                return Result<IReadOnlyList<Guid>>.Failure(Error.Validation("decision.bulk", $"1 to {MaxBulk} applications."));
            }

            Result<IReadOnlyList<ApplicationRecord>> offered = await seats.OfferAsync(listingId, count, ct).ConfigureAwait(false);
            if (!offered.IsSuccess)
            {
                return Result<IReadOnlyList<Guid>>.Failure(offered.Error);
            }

            await AuditAsync(Decided, org, [.. offered.Value.Select(a => a.Id)], "offer", ct, actor).ConfigureAwait(false);
            return Result<IReadOnlyList<Guid>>.Success([.. offered.Value.Select(a => a.Id)]);
        }, cancellationToken);

    private async Task<Result> ApplyAsync(OrganisationId org, UserId actor, ApplicationRecord application, string decision, string? note, CancellationToken ct)
    {
        if (note is { Length: > MaxNoteLength })
        {
            return Result.Failure(Error.Validation("decision.note", $"A note of at most {MaxNoteLength} characters."));
        }

        bool open = application.Status is ApplicationStatuses.Submitted or ApplicationStatuses.UnderReview;
        bool allowed = decision switch
        {
            Accept or Waitlist => open,
            Review => application.Status == ApplicationStatuses.Submitted,
            Decline => open || application.Status == ApplicationStatuses.Waitlisted,
            _ => false,
        };
        if (decision is not (Accept or Waitlist or Decline or Review))
        {
            return Result.Failure(Error.Validation("decision.kind", "One of accept, waitlist, decline or review."));
        }

        if (!allowed)
        {
            return Result.Failure(Error.Conflict("decision.not_allowed", "That decision doesn't fit where this application stands."));
        }

        Guid? youthUser = await db.FamilyMembers.Where(m => m.Id == application.FamilyMemberId).Select(m => m.UserId).FirstAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = clock.StoredNow();
        switch (decision)
        {
            case Accept:
                Result enrolled = await seats.EnrolAsync(application, org.Value, youthUser, ct).ConfigureAwait(false);
                if (!enrolled.IsSuccess)
                {
                    return enrolled;
                }

                application.Status = ApplicationStatuses.Accepted;
                application.DecidedAt = now;
                application.DecidedBy = actor.Value;
                break;
            case Waitlist:
                Result<int> placed = await seats.WaitlistAsync(application, org.Value, youthUser, ct).ConfigureAwait(false);
                if (!placed.IsSuccess)
                {
                    return Result.Failure(placed.Error);
                }

                application.Status = ApplicationStatuses.Waitlisted;
                application.DecidedBy = actor.Value;
                break;
            case Decline:
                await seats.ReleaseAsync(application, EnrollmentStatuses.Removed, "declined", ct).ConfigureAwait(false);
                application.Status = ApplicationStatuses.Declined;
                application.DecidedAt = now;
                application.DecidedBy = actor.Value;
                break;
            default:
                application.Status = ApplicationStatuses.UnderReview;
                break;
        }

        if (note is { Length: > 0 })
        {
            application.DecisionNote = note.Trim();
        }

        application.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(Decided, org, [application.Id], decision, ct, actor).ConfigureAwait(false);
        return Result.Success();
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<OrgApplicationView> ViewAsync(OrganisationId org, ApplicationRecord a, CancellationToken ct)
    {
        (string? first, string? last, DateOnly? dob) = await SharedAsync(org, a, ct).ConfigureAwait(false);
        EligibilityResult eligibility = JsonSerializer.Deserialize<EligibilityResult>(a.EligibilityResult, JsonSerializerOptions.Web) ?? new EligibilityResult(null, []);
        return new OrgApplicationView(a.Id, a.ListingId, first, last, dob, a.Status, a.SubmittedAt, eligibility,
            await sealedAnswers.ReadAsync(a.AnswersSnapshot, ct).ConfigureAwait(false), await PositionAsync(a, ct).ConfigureAwait(false), a.DecidedAt, a.DecisionNote);
    }

    /// <summary>The applicant as the family's live share for this organisation shows them: names and birth date, each only if shared.</summary>
    private async Task<(string? First, string? Last, DateOnly? DateOfBirth)> SharedAsync(OrganisationId org, ApplicationRecord a, CancellationToken ct)
    {
        DateTimeOffset now = clock.StoredNow();
        Guid member = a.FamilyMemberId;
        List<string[]> fields = await db.ProfileShares.AsNoTracking()
            .Where(s => s.FamilyProfileId == a.FamilyProfileId && s.OrgId == org.Value && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now)
                        && (s.ListingId == null || s.ListingId == a.ListingId) && s.MemberIds.Contains(member))
            .Select(s => s.SharedFields).ToListAsync(ct).ConfigureAwait(false);
        HashSet<string> shared = new(fields.SelectMany(f => f), StringComparer.Ordinal);
        if (shared.Count == 0)
        {
            return (null, null, null);
        }

        FamilyMemberRecord m = await db.FamilyMembers.AsNoTracking().FirstAsync(x => x.Id == member, ct).ConfigureAwait(false);
        bool names = shared.Contains(ProfileShareFields.MemberNames);
        string? dob = shared.Contains(ProfileShareFields.MemberBirthDates) ? await cipher.DecryptAsync(tenant.TenantId, m.DateOfBirthEnc, ct).ConfigureAwait(false) : null;
        return (
            names ? await cipher.DecryptAsync(tenant.TenantId, m.FirstNameEnc, ct).ConfigureAwait(false) : null,
            names ? await cipher.DecryptAsync(tenant.TenantId, m.LastNameEnc, ct).ConfigureAwait(false) : null,
            dob is null ? null : DateOnly.ParseExact(dob, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private async Task<int?> PositionAsync(ApplicationRecord a, CancellationToken ct) =>
        a.Status is ApplicationStatuses.Waitlisted or ApplicationStatuses.Offered
            ? await db.Enrollments.AsNoTracking().Where(e => e.ApplicationId == a.Id && e.Status == EnrollmentStatuses.Active)
                .Select(e => e.WaitlistPosition).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;

    private async Task<ApplicationRecord?> OwnAsync(OrganisationId org, Guid id, CancellationToken ct)
    {
        ApplicationRecord? application = await db.Applications.FirstOrDefaultAsync(a => a.Id == id && a.Status != ApplicationStatuses.Draft, ct).ConfigureAwait(false);
        return application is not null && await OwnsListingAsync(org, application.ListingId, ct).ConfigureAwait(false) ? application : null;
    }

    private async Task<bool> OwnsListingAsync(OrganisationId org, Guid listingId, CancellationToken ct) =>
        (await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org;

    private static SeatView View(SeatPoolRecord pool) => new(pool.Capacity, pool.SeatsTaken, pool.WaitlistCapacity, pool.WaitlistTaken);

    private Task AuditAsync(string action, OrganisationId org, IReadOnlyCollection<Guid> applications, string? decision, CancellationToken ct, UserId? actor = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "application",
            SubjectIds = applications,
            ActorOrgId = org,
            Detail = decision is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.Ordinal) { ["decision"] = decision },
        }, ct);

    private static Error NotFound() => Error.NotFound("decision.not_found", "Your organisation has no submitted application with that id.");

    private static Error NoListing() => Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id.");
}
