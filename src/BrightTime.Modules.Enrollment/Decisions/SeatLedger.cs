using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Decisions;

/// <summary>
/// A listing's seats and waitlist, kept true under concurrency (D48): every change takes the
/// pool's row <c>FOR UPDATE</c> first, then moves the enrolment and the counters together, in the
/// caller's transaction. <c>ck_seat_pool_taken</c> turns an over-booking that slips past into a
/// constraint violation rather than a silent oversell.
/// </summary>
/// <remarks>
/// A seat is held from the moment it is given: accepting takes one, and offering one to the
/// waitlist takes it too, so an offer never finds its seat gone. An offered place stays on the
/// waitlist row (kind and position) until the family answers.
/// </remarks>
public sealed class SeatLedger(EnrollmentDbContext db, ITenantContext tenant, IClock clock)
{
    public const string DefaultLabel = "default";

    /// <summary>The listing's pool, locked for the rest of the transaction; null when it has none.</summary>
    public async Task<SeatPoolRecord?> LockAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        (await db.SeatPools
            .FromSql($"select * from enrollment.seat_pool where listing_id = {listingId} and label = {DefaultLabel} for update")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Sets a listing's capacity, making its pool if it has none. Never below what is already taken.</summary>
    public async Task<Result<SeatPoolRecord>> SetAsync(Guid listingId, int capacity, int? waitlistCapacity, CancellationToken cancellationToken = default)
    {
        if (capacity is < 1 or > 10_000 || waitlistCapacity is < 0 or > 10_000)
        {
            return Result<SeatPoolRecord>.Failure(Error.Validation("seats.capacity", "1 to 10,000 seats, and a waitlist of 0 to 10,000 or no limit."));
        }

        SeatPoolRecord? pool = await LockAsync(listingId, cancellationToken).ConfigureAwait(false);
        if (pool is null)
        {
            pool = new SeatPoolRecord
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                ListingId = listingId,
                Label = DefaultLabel,
                NextWaitlistPosition = 1,
            };
            db.SeatPools.Add(pool);
        }

        if (capacity < pool.SeatsTaken || waitlistCapacity < pool.WaitlistTaken)
        {
            return Result<SeatPoolRecord>.Failure(Error.Conflict("seats.below_taken", "Fewer seats than are already taken. Release some first."));
        }

        pool.Capacity = capacity;
        pool.WaitlistCapacity = waitlistCapacity;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<SeatPoolRecord>.Success(pool);
    }

    /// <summary>Takes a seat and enrols the applicant.</summary>
    public async Task<Result> EnrolAsync(ApplicationRecord application, Guid org, Guid? youthUser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (await LockAsync(application.ListingId, cancellationToken).ConfigureAwait(false) is not { } pool)
        {
            return Result.Failure(NoSeats());
        }

        if (pool.SeatsTaken >= pool.Capacity)
        {
            return Result.Failure(Error.Conflict("seats.full", "Every seat is taken. Put the application on the waitlist instead."));
        }

        pool.SeatsTaken++;
        db.Enrollments.Add(New(application, pool, org, youthUser, EnrollmentKinds.Enrolled, null));
        application.SeatPoolId = pool.Id;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Puts the applicant at the back of the waitlist. Returns their position.</summary>
    public async Task<Result<int>> WaitlistAsync(ApplicationRecord application, Guid org, Guid? youthUser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (await LockAsync(application.ListingId, cancellationToken).ConfigureAwait(false) is not { } pool)
        {
            return Result<int>.Failure(NoSeats());
        }

        if (pool.WaitlistCapacity is { } limit && pool.WaitlistTaken >= limit)
        {
            return Result<int>.Failure(Error.Conflict("seats.waitlist_full", "The waitlist is full."));
        }

        int position = pool.NextWaitlistPosition++;
        pool.WaitlistTaken++;
        db.Enrollments.Add(New(application, pool, org, youthUser, EnrollmentKinds.Waitlist, position));
        application.SeatPoolId = pool.Id;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<int>.Success(position);
    }

    /// <summary>
    /// Offers free seats to the front of the waitlist, up to <paramref name="count"/>, and holds a
    /// seat for each. Returns the applications offered one.
    /// </summary>
    public async Task<Result<IReadOnlyList<ApplicationRecord>>> OfferAsync(Guid listingId, int count, CancellationToken cancellationToken = default)
    {
        if (await LockAsync(listingId, cancellationToken).ConfigureAwait(false) is not { } pool)
        {
            return Result<IReadOnlyList<ApplicationRecord>>.Failure(NoSeats());
        }

        int free = pool.Capacity - pool.SeatsTaken;
        if (free <= 0)
        {
            return Result<IReadOnlyList<ApplicationRecord>>.Failure(Error.Conflict("seats.full", "Every seat is taken."));
        }

        List<ApplicationRecord> next = await (from e in db.Enrollments
                                              join a in db.Applications on e.ApplicationId equals a.Id
                                              where e.SeatPoolId == pool.Id && e.SeatKind == EnrollmentKinds.Waitlist && e.Status == EnrollmentStatuses.Active
                                                    && a.Status == ApplicationStatuses.Waitlisted
                                              orderby e.WaitlistPosition
                                              select a).Take(Math.Min(count, free)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (next.Count == 0)
        {
            return Result<IReadOnlyList<ApplicationRecord>>.Failure(Error.Conflict("seats.no_waitlist", "Nobody is waiting."));
        }

        DateTimeOffset now = clock.StoredNow();
        foreach (ApplicationRecord application in next)
        {
            pool.SeatsTaken++;
            pool.WaitlistTaken--;
            application.Status = ApplicationStatuses.Offered;
            application.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<ApplicationRecord>>.Success(next);
    }

    /// <summary>The family takes an offered seat: the held place becomes an enrolment.</summary>
    public async Task TakeOfferAsync(ApplicationRecord application, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        await LockAsync(application.ListingId, cancellationToken).ConfigureAwait(false);
        EnrollmentRecord place = await PlaceAsync(application, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An offered application holds a place.");
        place.SeatKind = EnrollmentKinds.Enrolled;
        place.WaitlistPosition = null;
        place.EnrolledAt = clock.StoredNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives back whatever an application holds: an enrolled seat, an offered (held) seat, or a
    /// waitlist place. Ends its enrolment row with <paramref name="status"/> and the reason.
    /// </summary>
    public async Task ReleaseAsync(ApplicationRecord application, string status, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.Status is not (ApplicationStatuses.Accepted or ApplicationStatuses.Offered or ApplicationStatuses.Waitlisted))
        {
            return;
        }

        SeatPoolRecord pool = await LockAsync(application.ListingId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An application holding a place has a pool.");
        if (await PlaceAsync(application, cancellationToken).ConfigureAwait(false) is { } place)
        {
            place.Status = status;
            place.EndedAt = clock.StoredNow();
            place.EndReason = reason;
        }

        if (application.Status == ApplicationStatuses.Waitlisted)
        {
            pool.WaitlistTaken--;
        }
        else
        {
            pool.SeatsTaken--;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<EnrollmentRecord?> PlaceAsync(ApplicationRecord application, CancellationToken ct) =>
        db.Enrollments.FirstOrDefaultAsync(e => e.ApplicationId == application.Id && e.Status == EnrollmentStatuses.Active, ct);

    private EnrollmentRecord New(ApplicationRecord application, SeatPoolRecord pool, Guid org, Guid? youthUser, string kind, int? position) => new()
    {
        Id = UuidV7.NewId(),
        TenantId = application.TenantId,
        ListingId = application.ListingId,
        SeatPoolId = pool.Id,
        ApplicationId = application.Id,
        FamilyMemberId = application.FamilyMemberId,
        YouthUserId = youthUser,
        OrgId = org,
        SeatKind = kind,
        WaitlistPosition = position,
        Status = EnrollmentStatuses.Active,
        EnrolledAt = clock.StoredNow(),
    };

    private static Error NoSeats() => Error.Precondition("seats.none", "Set this listing's seats first.");
}
