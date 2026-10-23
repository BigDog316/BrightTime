using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Portfolio;

/// <summary>
/// Keeps a young person's participation in step with their attendance (D65). One record per
/// enrolment, rebuilt whole from the current records each time, so a correction, a replayed sync or
/// a redelivered message leaves the same row.
/// </summary>
/// <remarks>
/// <para>
/// A record belongs to the young person's own account. An enrolment the family hasn't linked to
/// one has no record, and unlinking takes it away. An enrolment with no session attended has none
/// either: a portfolio shows what someone took part in, not what they signed up for.
/// </para>
/// <para>
/// Hours are the minutes recorded where staff recorded them, and the meeting's length otherwise.
/// The title and organisation are copied, so the record outlives the listing.
/// </para>
/// </remarks>
public sealed class ParticipationProjector(
    LearningDbContext db,
    ITenantContext tenant,
    IClock clock,
    IEnrolmentQueries enrolments,
    IAttendanceQueries attendance,
    IListingOccurrences occurrences,
    IListingDetails listings,
    IDirectoryQueries directory)
{
    /// <summary>States that count as taking part: there for at least part of the meeting.</summary>
    private static readonly string[] Attended = [AttendanceStates.Present, AttendanceStates.Late, AttendanceStates.LeftEarly];

    public Task ProjectAsync(Guid enrollmentId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            DateOnly today = Day(clock.UtcNow);
            IReadOnlyList<EnrolmentFacts> found = await enrolments.FactsAsync([enrollmentId], today, ct).ConfigureAwait(false);
            EnrolmentFacts? facts = found.Count > 0 ? found[0] : null;
            ParticipationRecordRecord? record = await db.Participation.FirstOrDefaultAsync(p => p.EnrollmentId == enrollmentId, ct).ConfigureAwait(false);
            List<AttendanceFact> taken = facts?.YouthUserId is null ? [] :
                [.. (await attendance.ForEnrollmentAsync(enrollmentId, ct).ConfigureAwait(false)).Where(a => Attended.Contains(a.State))];

            if (facts?.YouthUserId is not { } owner || taken.Count == 0)
            {
                if (record is not null)
                {
                    Guid previous = record.UserId;
                    db.Participation.Remove(record);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await TotalAsync(previous, ct).ConfigureAwait(false);
                }

                return true;
            }

            // Each meeting's length, for the sessions staff didn't record minutes for.
            IReadOnlyList<ListingTime> meetings = await occurrences.GetAsync(facts.ListingId, taken[0].OccurrenceStartsAt.AddSeconds(-1), taken[^1].OccurrenceStartsAt.AddSeconds(1), ct).ConfigureAwait(false);
            Dictionary<DateTimeOffset, double> lengths = meetings.GroupBy(m => m.StartsAt).ToDictionary(g => g.Key, g => (g.First().EndsAt - g.Key).TotalMinutes);
            double minutes = taken.Sum(a => a.MinutesAttended is { } m ? m : lengths.GetValueOrDefault(a.OccurrenceStartsAt));
            decimal hours = Math.Min(Math.Round((decimal)minutes / 60m, 2), 10000m);

            string? title = (await listings.GetForReviewAsync(tenant.TenantId, facts.ListingId, tenant.Locale.Code, ct).ConfigureAwait(false))?.Title;
            string? orgName = await directory.GetOrganisationNameAsync(new OrganisationId(facts.OrgId), ct).ConfigureAwait(false);
            Guid? before = record?.UserId;
            if (record is null)
            {
                record = new ParticipationRecordRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    EnrollmentId = enrollmentId,
                    TitleSnapshot = title ?? string.Empty,
                    Source = ParticipationSources.Attendance,
                    CreatedAt = clock.StoredNow(),
                };
                db.Participation.Add(record);
            }

            record.UserId = owner;
            record.ListingId = facts.ListingId;
            record.OrgId = facts.OrgId;
            record.TitleSnapshot = title ?? record.TitleSnapshot;
            record.OrgNameSnapshot = orgName ?? record.OrgNameSnapshot;
            record.StartedOn = Day(taken[0].OccurrenceStartsAt);
            record.EndedOn = facts.EndedAt is { } ended && Day(ended) >= record.StartedOn ? Day(ended) : null;
            record.SessionsAttended = taken.Count;
            record.Hours = hours;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await TotalAsync(owner, ct).ConfigureAwait(false);
            if (before is { } moved && moved != owner)
            {
                await TotalAsync(moved, ct).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken);

    /// <summary>A portfolio's total: the hours of every record its owner hasn't hidden.</summary>
    internal async Task TotalAsync(Guid user, CancellationToken ct)
    {
        decimal total = await db.Participation.Where(p => p.UserId == user && !p.IsHidden).SumAsync(p => p.Hours, ct).ConfigureAwait(false);
        await db.Profiles.Where(p => p.UserId == user).ExecuteUpdateAsync(s => s.SetProperty(p => p.TotalHours, Math.Min(total, 999999m)), ct).ConfigureAwait(false);
    }

    private DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tenant.TimeZone).DateTime);
}

/// <summary>An enrolment's attendance changed: its participation follows.</summary>
public sealed class AttendanceChangedHandler(ParticipationProjector projector) : IModuleMessageHandler<AttendanceChanged>
{
    public Task HandleAsync(AttendanceChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.EnrollmentId, cancellationToken);
    }
}

/// <summary>An enrolment was linked to a young person's account, or unlinked: its participation follows the account.</summary>
public sealed class EnrolmentAccountChangedHandler(ParticipationProjector projector) : IModuleMessageHandler<EnrolmentAccountChanged>
{
    public Task HandleAsync(EnrolmentAccountChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.EnrollmentId, cancellationToken);
    }
}
