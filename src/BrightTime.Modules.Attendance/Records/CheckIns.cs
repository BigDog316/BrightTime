using System.Security.Cryptography;
using System.Text;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Attendance.Records;

/// <summary>A check-in code to show as a QR, and when it stops working.</summary>
public sealed record CheckinCode(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Check-in by QR (M3b, D50), in both directions:
/// <list type="bullet">
/// <item>a <b>badge</b>: the family's app shows a code for one enrolment, single-use and good for
/// two minutes; staff scan it to record the young person present;</item>
/// <item>a <b>kiosk</b>: a staff screen shows a code for a listing, good for a minute and for
/// anyone; a family scans it to check their own young person in to the meeting on now.</item>
/// </list>
/// </summary>
/// <remarks>
/// A code is 256 random bits, kept only as its SHA-256, and carries no identifier at all
/// (docs/plan/07): a photographed badge names nobody, and is spent or stale within minutes. Both
/// paths write the record through <see cref="AttendanceRecords"/>, under its checks and audit.
/// </remarks>
public sealed class CheckIns(
    AttendanceDbContext db,
    ITenantContext tenant,
    IClock clock,
    IListingDetails listings,
    IListingOccurrences occurrences,
    IEnrolmentQueries enrolments,
    AttendanceRecords records)
{
    public const string YouthBadge = "youth_badge";
    public const string SessionKiosk = "session_kiosk";

    public static readonly TimeSpan BadgeLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan KioskLifetime = TimeSpan.FromSeconds(60);

    /// <summary>A badge for one of the family's own enrolments.</summary>
    public Task<Result<CheckinCode>> BadgeAsync(UserId owner, Guid enrollmentId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            FamilyEnrolment? mine = (await enrolments.ForOwnerAsync(owner.Value, ct).ConfigureAwait(false))
                .FirstOrDefault(e => e.EnrollmentId == enrollmentId && e.Status == EnrollmentStatuses.Active);
            return mine is null
                ? Result<CheckinCode>.Failure(Error.NotFound("checkin.enrolment", "None of your family's current enrolments has that id."))
                : Result<CheckinCode>.Success(await IssueAsync(YouthBadge, enrollmentId, mine.ListingId, BadgeLifetime, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>A kiosk code for one of the organisation's listings, for its staff to show.</summary>
    public Task<Result<CheckinCode>> KioskAsync(OrganisationId org, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            (await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org
                ? Result<CheckinCode>.Success(await IssueAsync(SessionKiosk, null, listingId, KioskLifetime, ct).ConfigureAwait(false))
                : Result<CheckinCode>.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id.")), cancellationToken);

    /// <summary>Staff scan a family's badge: the code is spent, and its young person is present at the meeting given.</summary>
    public Task<Result<AttendanceView>> ScanBadgeAsync(OrganisationId org, UserId staff, string token, DateTimeOffset occurrence, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await FindAsync(token, YouthBadge, ct).ConfigureAwait(false) is not { } code)
            {
                return Result<AttendanceView>.Failure(Invalid());
            }

            if (Refusal(code) is { } refused)
            {
                return Result<AttendanceView>.Failure(refused);
            }

            // Spent once, whatever happens next: a code that fails a check isn't tried again.
            int spent = await db.CheckinTokens.Where(t => t.Id == code.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, clock.StoredNow()).SetProperty(t => t.ConsumedBy, staff.Value), ct).ConfigureAwait(false);
            return spent == 0
                ? Result<AttendanceView>.Failure(Used())
                : await records.CheckInAsync(org, staff, code.EnrollmentId!.Value, occurrence, AttendanceMethods.Qr, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>A family scans a kiosk's code: their young person is present at the listing's meeting on now.</summary>
    public Task<Result<AttendanceView>> KioskCheckInAsync(UserId owner, string token, Guid enrollmentId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await FindAsync(token, SessionKiosk, ct).ConfigureAwait(false) is not { } code)
            {
                return Result<AttendanceView>.Failure(Invalid());
            }

            if (Refusal(code) is { } refused)
            {
                return Result<AttendanceView>.Failure(refused);
            }

            FamilyEnrolment? mine = (await enrolments.ForOwnerAsync(owner.Value, ct).ConfigureAwait(false))
                .FirstOrDefault(e => e.EnrollmentId == enrollmentId && e.Status == EnrollmentStatuses.Active && e.ListingId == code.ListingId);
            if (mine is null)
            {
                return Result<AttendanceView>.Failure(Error.NotFound("checkin.enrolment", "None of your family's current enrolments is for this activity."));
            }

            if (await MeetingNowAsync(code.ListingId!.Value, ct).ConfigureAwait(false) is not { } meeting)
            {
                return Result<AttendanceView>.Failure(Error.Conflict("checkin.no_meeting", "This activity isn't meeting now."));
            }

            EnrolmentSummary enrolment = (await enrolments.GetAsync([enrollmentId], ct).ConfigureAwait(false))[0];
            return await records.CheckInAsync(new OrganisationId(enrolment.OrgId), owner, enrollmentId, meeting.StartsAt, AttendanceMethods.Kiosk, ct).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<CheckinCode> IssueAsync(string kind, Guid? enrollmentId, Guid listingId, TimeSpan lifetime, CancellationToken ct)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DateTimeOffset now = clock.StoredNow();
        CheckinTokenRecord code = new()
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            EnrollmentId = enrollmentId,
            ListingId = listingId,
            TokenHash = Hash(token),
            TokenKind = kind,
            IssuedAt = now,
            ExpiresAt = now + lifetime,
        };
        db.CheckinTokens.Add(code);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CheckinCode(token, code.ExpiresAt);
    }

    private async Task<CheckinTokenRecord?> FindAsync(string token, string kind, CancellationToken ct)
    {
        if (token is not { Length: 43 })
        {
            return null;
        }

        byte[] hash = Hash(token);
        return await db.CheckinTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash && t.TokenKind == kind, ct).ConfigureAwait(false);
    }

    private Error? Refusal(CheckinTokenRecord code) =>
        code.ConsumedAt is not null ? Used()
        : code.ExpiresAt <= clock.StoredNow() ? Error.Conflict("checkin.expired", "That code has run out. Show a fresh one.")
        : null;

    /// <summary>The listing's meeting under way now, or starting within the hour.</summary>
    private async Task<ListingTime?> MeetingNowAsync(Guid listingId, CancellationToken ct)
    {
        DateTimeOffset now = clock.StoredNow();
        IReadOnlyList<ListingTime> near = await occurrences.GetAsync(listingId, now.AddHours(-12), now.AddHours(1), ct).ConfigureAwait(false);
        return near.Where(m => m.EndsAt >= now).OrderBy(m => m.StartsAt).FirstOrDefault();
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private static Error Invalid() => Error.NotFound("checkin.invalid", "That isn't a check-in code.");

    private static Error Used() => Error.Conflict("checkin.used", "That code has already been used. Show a fresh one.");
}
