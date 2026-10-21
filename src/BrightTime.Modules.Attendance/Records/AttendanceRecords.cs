using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Devices;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Attendance.Records;

public sealed record AttendanceInput(
    Guid EnrollmentId,
    DateTimeOffset OccurrenceStartsAt,
    string State,
    short? MinutesAttended = null,
    DateTimeOffset? CheckedInAt = null,
    DateTimeOffset? CheckedOutAt = null,
    string? Note = null);

/// <summary>One record captured offline: the device's own id for it, so a replay is recognised, and when it was taken.</summary>
public sealed record SyncItem(Guid ClientRecordId, AttendanceInput Record, DateTimeOffset? CapturedAt);

[YouthData]
public sealed record RosterLine(Guid EnrollmentId, string? DisplayName, Guid? RecordId, string? State, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt);

[YouthData]
public sealed record RosterView(Guid ListingId, DateTimeOffset OccurrenceStartsAt, DateTimeOffset OccurrenceEndsAt, IReadOnlyList<RosterLine> Lines);

[YouthData]
public sealed record AttendanceView(
    Guid Id,
    Guid EnrollmentId,
    Guid ListingId,
    DateTimeOffset OccurrenceStartsAt,
    string State,
    short? MinutesAttended,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    string Method,
    DateTimeOffset RecordedAt,
    string? Note,
    Guid? SupersededBy);

public sealed record SyncOutcome(Guid ClientRecordId, string Status, Guid? ServerId, string? Reason);

[YouthData]
public sealed record FamilyAttendance(Guid EnrollmentId, string MemberFirstName, Guid ListingId, DateTimeOffset OccurrenceStartsAt, string State, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt);

/// <summary>
/// Attendance (M3b, D49): rosters per meeting, records taken one at a time or synced from a phone
/// that was offline, and corrections that supersede rather than edit.
/// </summary>
/// <remarks>
/// <para>
/// Attendance says where a named child was at a given hour (docs/plan/07, T4), so: the caller
/// has checked the actor works for the organisation; this class finds only the organisation's
/// enrolments; every read and write is audited with the organisation as the actor's; the free-text
/// note is encrypted; and a roster shows a display name only where the family's share names the
/// young person.
/// </para>
/// <para>
/// A record is for a meeting that exists, isn't cancelled, starts no more than an hour from now and
/// no more than <see cref="MaxAgeDays"/> days ago, and for an active enrolment in a seat. Writes for
/// one organisation are serialised by an advisory lock, so two phones syncing the same meeting
/// can't race past <c>ux_attendance_live</c>; a replayed item is recognised by
/// <c>(device_id, client_record_id)</c> and changes nothing.
/// </para>
/// </remarks>
public sealed class AttendanceRecords(
    AttendanceDbContext db,
    IFieldCipher cipher,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IListingDetails listings,
    IListingOccurrences occurrences,
    IEnrolmentQueries enrolments,
    KioskDevices devices)
{
    public const int MaxAgeDays = 30;
    public const int MaxBatch = 500;
    public const int MaxNoteLength = 1000;
    public const int MaxRangeDays = 31;

    /// <summary>After an enrolment ends, its records stay visible to the organisation this long (youth_access_grant's default grace).</summary>
    public const int GraceDays = 180;

    public const string RosterRead = "attendance.roster.read";
    public const string Read = "attendance.read";
    public const string Recorded = "attendance.recorded";
    public const string Corrected = "attendance.corrected";
    public const string Synced = "attendance.synced";
    public const string FamilyRead = "attendance.family_read";

    // ---- rosters -----------------------------------------------------------------------------

    /// <summary>A meeting's roster: every enrolled young person, with their record for it if there is one.</summary>
    public Task<Result<RosterView>> RosterAsync(OrganisationId org, Guid listingId, DateTimeOffset occurrence, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if ((await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId != org)
            {
                return Result<RosterView>.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            if (await MeetingAsync(listingId, occurrence, ct).ConfigureAwait(false) is not { } meeting)
            {
                return Result<RosterView>.Failure(NoMeeting());
            }

            IReadOnlyList<RosterEntry> roster = await enrolments.RosterAsync(listingId, ct).ConfigureAwait(false);
            Guid[] ids = [.. roster.Select(r => r.EnrollmentId)];
            Dictionary<Guid, AttendanceRecordRecord> taken = await db.Records.AsNoTracking()
                .Where(r => ids.Contains(r.EnrollmentId) && r.OccurrenceStartsAt == meeting.StartsAt && r.SupersededBy == null)
                .ToDictionaryAsync(r => r.EnrollmentId, ct).ConfigureAwait(false);
            List<RosterLine> lines = [.. roster.Select(r => taken.TryGetValue(r.EnrollmentId, out AttendanceRecordRecord? rec)
                ? new RosterLine(r.EnrollmentId, r.DisplayName, rec.Id, rec.State, rec.CheckedInAt, rec.CheckedOutAt)
                : new RosterLine(r.EnrollmentId, r.DisplayName, null, null, null, null))];
            await AuditAsync(RosterRead, org, ids, ct).ConfigureAwait(false);
            return Result<RosterView>.Success(new RosterView(listingId, meeting.StartsAt, meeting.EndsAt, lines));
        }, cancellationToken);

    /// <summary>The organisation's meetings in a window of up to a week: what a staff member can take a roster for.</summary>
    public async Task<Result<IReadOnlyList<OrganisationMeeting>>> MeetingsAsync(OrganisationId org, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        to <= from || to - from > TimeSpan.FromDays(7)
            ? Result<IReadOnlyList<OrganisationMeeting>>.Failure(Error.Validation("attendance.range", "A window of up to 7 days."))
            : Result<IReadOnlyList<OrganisationMeeting>>.Success(await occurrences.ForOrganisationAsync(org.Value, from, to, cancellationToken).ConfigureAwait(false));

    // ---- writing -----------------------------------------------------------------------------

    public Task<Result<AttendanceView>> RecordAsync(OrganisationId org, UserId actor, AttendanceInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            await LockAsync(org, ct).ConfigureAwait(false);
            Checked check = await CheckAsync(org, input, [], ct).ConfigureAwait(false);
            if (check.Error is { } invalid)
            {
                return Result<AttendanceView>.Failure(invalid);
            }

            if (await LiveAsync(input.EnrollmentId, check.StartsAt, ct).ConfigureAwait(false) is { } live)
            {
                return Result<AttendanceView>.Failure(Error.Conflict("attendance.exists", "This young person already has a record for this meeting. Correct it instead.",
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["recordId"] = live.Id }));
            }

            AttendanceRecordRecord record = await NewAsync(org, actor, input, check, AttendanceMethods.Manual, clock.StoredNow(), null, null, ct).ConfigureAwait(false);
            db.Records.Add(record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Recorded, org, [input.EnrollmentId], ct).ConfigureAwait(false);
            return Result<AttendanceView>.Success(await ViewAsync(record, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// A check-in by code (D50): present, checked in now, by the method given, under the same
    /// checks as any record. The caller has proved the code; this writes the record.
    /// </summary>
    internal async Task<Result<AttendanceView>> CheckInAsync(OrganisationId org, UserId actor, Guid enrollmentId, DateTimeOffset occurrence, string method, CancellationToken ct)
    {
        await LockAsync(org, ct).ConfigureAwait(false);
        DateTimeOffset now = clock.StoredNow();
        AttendanceInput input = new(enrollmentId, occurrence, AttendanceStates.Present, CheckedInAt: now);
        Checked check = await CheckAsync(org, input, [], ct).ConfigureAwait(false);
        if (check.Error is { } invalid)
        {
            return Result<AttendanceView>.Failure(invalid);
        }

        if (await LiveAsync(enrollmentId, check.StartsAt, ct).ConfigureAwait(false) is { } live)
        {
            return Result<AttendanceView>.Failure(Error.Conflict("attendance.exists", "This young person already has a record for this meeting. Correct it instead.",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["recordId"] = live.Id }));
        }

        AttendanceRecordRecord record = await NewAsync(org, actor, input, check, method, now, null, null, ct).ConfigureAwait(false);
        db.Records.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(Recorded, org, [enrollmentId], ct, new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = method }).ConfigureAwait(false);
        return Result<AttendanceView>.Success(await ViewAsync(record, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A phone's offline queue, item by item: applied, a duplicate of an earlier replay (a success),
    /// a conflict with a record someone else made first (theirs stands), or rejected with a reason.
    /// </summary>
    public Task<Result<IReadOnlyList<SyncOutcome>>> SyncAsync(OrganisationId org, UserId actor, string deviceId, string? appVersion, IReadOnlyList<SyncItem> items, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(items);
            string? device = deviceId?.Trim();
            if (device is not { Length: > 0 and <= 100 } || device.Any(char.IsControl))
            {
                return Result<IReadOnlyList<SyncOutcome>>.Failure(Error.Validation("attendance.device", "A device id of 1 to 100 characters."));
            }

            if (items.Count is 0 or > MaxBatch)
            {
                return Result<IReadOnlyList<SyncOutcome>>.Failure(Error.Validation("attendance.batch", $"1 to {MaxBatch} records."));
            }

            // Only a registered, unrevoked device syncs (D51): a lost phone, once revoked, changes nothing.
            if (await devices.RequireActiveAsync(org, device, ct).ConfigureAwait(false) is { } refused)
            {
                return Result<IReadOnlyList<SyncOutcome>>.Failure(refused);
            }

            await LockAsync(org, ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            Dictionary<(Guid, DateTimeOffset), ListingTime?> meetings = [];
            List<SyncOutcome> outcomes = [];
            HashSet<Guid> touched = [];
            foreach (SyncItem item in items)
            {
                if (item?.Record is null)
                {
                    continue;
                }

                if (await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.DeviceId == device && r.ClientRecordId == item.ClientRecordId, ct).ConfigureAwait(false) is { } replayed)
                {
                    outcomes.Add(new SyncOutcome(item.ClientRecordId, SyncStatuses.Duplicate, replayed.Id, null));
                    continue;
                }

                Checked check = await CheckAsync(org, item.Record, meetings, ct).ConfigureAwait(false);
                if (check.Error is { } invalid)
                {
                    outcomes.Add(new SyncOutcome(item.ClientRecordId, SyncStatuses.Rejected, null, invalid.Code));
                    continue;
                }

                if (await LiveAsync(item.Record.EnrollmentId, check.StartsAt, ct).ConfigureAwait(false) is { } live)
                {
                    outcomes.Add(new SyncOutcome(item.ClientRecordId, SyncStatuses.Conflict, live.Id, "attendance.exists"));
                    continue;
                }

                DateTimeOffset captured = item.CapturedAt is { } at && at < now ? at : now;
                AttendanceRecordRecord record = await NewAsync(org, actor, item.Record, check, AttendanceMethods.OfflineSync, captured, device, item.ClientRecordId, ct).ConfigureAwait(false);
                db.Records.Add(record);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                touched.Add(item.Record.EnrollmentId);
                outcomes.Add(new SyncOutcome(item.ClientRecordId, SyncStatuses.Applied, record.Id, null));
            }

            List<DateTimeOffset> times = [.. items.Where(i => i?.CapturedAt is not null).Select(i => i.CapturedAt!.Value)];
            db.SyncBatches.Add(new AttendanceSyncBatchRecord
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                DeviceId = device,
                SubmittedBy = actor.Value,
                ReceivedAt = now,
                CapturedFrom = times.Count > 0 ? times.Min() : null,
                CapturedTo = times.Count > 0 ? times.Max() : null,
                ItemCount = outcomes.Count,
                AppliedCount = outcomes.Count(o => o.Status == SyncStatuses.Applied),
                ConflictCount = outcomes.Count(o => o.Status == SyncStatuses.Conflict),
                AppVersion = appVersion is { Length: <= 40 } ? appVersion : null,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Synced, org, [.. touched], ct, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device"] = device,
                ["applied"] = outcomes.Count(o => o.Status == SyncStatuses.Applied).ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
            return Result<IReadOnlyList<SyncOutcome>>.Success(outcomes);
        }, cancellationToken);

    /// <summary>
    /// Corrects a live record by superseding it: the old one stays, pointing at its replacement, so
    /// the history of what was recorded, and by whom, is never lost.
    /// </summary>
    public Task<Result<AttendanceView>> CorrectAsync(OrganisationId org, UserId actor, Guid recordId, AttendanceInput input, string reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            string? why = reason?.Trim();
            if (why is not { Length: > 0 and <= 500 })
            {
                return Result<AttendanceView>.Failure(Error.Validation("attendance.reason", "Say why, in up to 500 characters."));
            }

            await LockAsync(org, ct).ConfigureAwait(false);
            AttendanceRecordRecord? old = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId && r.OrgId == org.Value, ct).ConfigureAwait(false);
            if (old is null)
            {
                return Result<AttendanceView>.Failure(Error.NotFound("attendance.not_found", "Your organisation has no attendance record with that id."));
            }

            if (old.SupersededBy is not null)
            {
                return Result<AttendanceView>.Failure(Error.Conflict("attendance.superseded", "That record has already been corrected. Correct the newer one."));
            }

            AttendanceInput corrected = input with { EnrollmentId = old.EnrollmentId, OccurrenceStartsAt = old.OccurrenceStartsAt };
            if (Shape(corrected) is { } invalid)
            {
                return Result<AttendanceView>.Failure(invalid);
            }

            // The live index allows one current record per meeting: the old one leaves it (pointing at
            // itself, which the foreign key accepts) before the new one arrives, then points onward.
            await db.Records.Where(r => r.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SupersededBy, old.Id), ct).ConfigureAwait(false);
            AttendanceRecordRecord record = new()
            {
                Id = UuidV7.NewId(),
                TenantId = old.TenantId,
                OrgId = old.OrgId,
                ListingId = old.ListingId,
                EnrollmentId = old.EnrollmentId,
                OccurrenceStartsAt = old.OccurrenceStartsAt,
                State = corrected.State,
                MinutesAttended = corrected.MinutesAttended,
                CheckedInAt = corrected.CheckedInAt,
                CheckedOutAt = corrected.CheckedOutAt,
                Method = AttendanceMethods.Manual,
                RecordedBy = actor.Value,
                RecordedAt = clock.StoredNow(),
                NoteEnc = await cipher.EncryptAsync(tenant.TenantId, Blank(corrected.Note), ct).ConfigureAwait(false),
            };
            db.Records.Add(record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.Records.Where(r => r.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SupersededBy, record.Id), ct).ConfigureAwait(false);
            await AuditAsync(Corrected, org, [old.EnrollmentId], ct, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["record"] = old.Id.ToString(),
                ["reason"] = why,
            }).ConfigureAwait(false);
            return Result<AttendanceView>.Success(await ViewAsync(record, ct).ConfigureAwait(false));
        }, cancellationToken);

    // ---- reading -----------------------------------------------------------------------------

    /// <summary>
    /// The organisation's records for meetings in a window of up to <see cref="MaxRangeDays"/> days,
    /// current ones only unless the history is asked for. Records of an enrolment that ended more
    /// than <see cref="GraceDays"/> days ago are left out (docs/plan/07, T4).
    /// </summary>
    public Task<Result<IReadOnlyList<AttendanceView>>> ListAsync(OrganisationId org, DateTimeOffset from, DateTimeOffset to, Guid? listingId, bool history, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(MaxRangeDays))
            {
                return Result<IReadOnlyList<AttendanceView>>.Failure(Error.Validation("attendance.range", $"A window of up to {MaxRangeDays} days."));
            }

            List<AttendanceRecordRecord> records = await db.Records.AsNoTracking()
                .Where(r => r.OrgId == org.Value && r.OccurrenceStartsAt >= from && r.OccurrenceStartsAt < to
                            && (listingId == null || r.ListingId == listingId) && (history || r.SupersededBy == null))
                .OrderBy(r => r.OccurrenceStartsAt).ThenBy(r => r.RecordedAt).Take(5000).ToListAsync(ct).ConfigureAwait(false);
            DateTimeOffset cutoff = clock.StoredNow().AddDays(-GraceDays);
            HashSet<Guid> visible = [.. (await enrolments.GetAsync([.. records.Select(r => r.EnrollmentId)], ct).ConfigureAwait(false))
                .Where(e => e.OrgId == org.Value && (e.EndedAt is null || e.EndedAt > cutoff)).Select(e => e.Id)];
            List<AttendanceView> views = [];
            foreach (AttendanceRecordRecord r in records.Where(r => visible.Contains(r.EnrollmentId)))
            {
                views.Add(await ViewAsync(r, ct).ConfigureAwait(false));
            }

            await AuditAsync(Read, org, [.. visible], ct).ConfigureAwait(false);
            return Result<IReadOnlyList<AttendanceView>>.Success(views);
        }, cancellationToken);

    /// <summary>The asking family's own attendance: everyone on their profile, current records, newest first. Staff notes are never shown here.</summary>
    public Task<IReadOnlyList<FamilyAttendance>> ForFamilyAsync(UserId owner, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<FamilyAttendance>>(async ct =>
        {
            IReadOnlyList<FamilyEnrolment> mine = await enrolments.ForOwnerAsync(owner.Value, ct).ConfigureAwait(false);
            Dictionary<Guid, FamilyEnrolment> byId = mine.ToDictionary(e => e.EnrollmentId);
            Guid[] ids = [.. byId.Keys];
            List<AttendanceRecordRecord> records = await db.Records.AsNoTracking()
                .Where(r => ids.Contains(r.EnrollmentId) && r.SupersededBy == null)
                .OrderByDescending(r => r.OccurrenceStartsAt).Take(500).ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = FamilyRead, SubjectKind = "enrollment", SubjectIds = ids }, ct).ConfigureAwait(false);
            return [.. records.Select(r => new FamilyAttendance(r.EnrollmentId, byId[r.EnrollmentId].MemberFirstName, r.ListingId, r.OccurrenceStartsAt, r.State, r.CheckedInAt, r.CheckedOutAt))];
        }, cancellationToken);

    // ---- checks ------------------------------------------------------------------------------

    private readonly record struct Checked(Error? Error, DateTimeOffset StartsAt, Guid ListingId);

    private async Task<Checked> CheckAsync(OrganisationId org, AttendanceInput input, Dictionary<(Guid, DateTimeOffset), ListingTime?> meetings, CancellationToken ct)
    {
        if (Shape(input) is { } invalid)
        {
            return new Checked(invalid, default, default);
        }

        IReadOnlyList<EnrolmentSummary> found = await enrolments.GetAsync([input.EnrollmentId], ct).ConfigureAwait(false);
        EnrolmentSummary? enrolment = found.Count > 0 ? found[0] : null;
        if (enrolment is null || enrolment.OrgId != org.Value || enrolment.Status != EnrollmentStatuses.Active || enrolment.SeatKind != EnrollmentKinds.Enrolled)
        {
            return new Checked(Error.Validation("attendance.enrolment", "An active enrolment at your organisation."), default, default);
        }

        if (!meetings.TryGetValue((enrolment.ListingId, input.OccurrenceStartsAt), out ListingTime? meeting))
        {
            meeting = await MeetingAsync(enrolment.ListingId, input.OccurrenceStartsAt, ct).ConfigureAwait(false);
            meetings[(enrolment.ListingId, input.OccurrenceStartsAt)] = meeting;
        }

        if (meeting is null)
        {
            return new Checked(NoMeeting(), default, default);
        }

        DateTimeOffset now = clock.StoredNow();
        if (meeting.StartsAt > now.AddHours(1))
        {
            return new Checked(Error.Validation("attendance.too_early", "That meeting hasn't started yet."), default, default);
        }

        return meeting.StartsAt < now.AddDays(-MaxAgeDays)
            ? new Checked(Error.Validation("attendance.too_late", $"Records can be made for up to {MaxAgeDays} days after a meeting."), default, default)
            : new Checked(null, meeting.StartsAt, enrolment.ListingId);
    }

    private static Error? Shape(AttendanceInput input)
    {
        if (input.State is null || !AttendanceStates.All.Contains(input.State))
        {
            return Error.Validation("attendance.state", "One of present, absent, late, excused, left_early or unknown.");
        }

        if (input.MinutesAttended is < 0 or > 1440 || (input.CheckedInAt is { } inAt && input.CheckedOutAt is { } outAt && outAt < inAt))
        {
            return Error.Validation("attendance.times", "Minutes from 0 to 1440, and a check-out no earlier than the check-in.");
        }

        return input.Note is { Length: > MaxNoteLength }
            ? Error.Validation("attendance.note", $"A note of at most {MaxNoteLength} characters.")
            : null;
    }

    /// <summary>The listing's meeting starting at <paramref name="startsAt"/> (to the second), or null.</summary>
    private async Task<ListingTime?> MeetingAsync(Guid listingId, DateTimeOffset startsAt, CancellationToken ct)
    {
        IReadOnlyList<ListingTime> found = await occurrences.GetAsync(listingId, startsAt.AddSeconds(-1), startsAt.AddSeconds(1), ct).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    private Task<AttendanceRecordRecord?> LiveAsync(Guid enrollment, DateTimeOffset startsAt, CancellationToken ct) =>
        db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.EnrollmentId == enrollment && r.OccurrenceStartsAt == startsAt && r.SupersededBy == null, ct);

    /// <summary>Serialises the organisation's attendance writes for the rest of the transaction.</summary>
    private Task<int> LockAsync(OrganisationId org, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtextextended({"attendance:" + org.Value}, 0))", ct);

    private async Task<AttendanceRecordRecord> NewAsync(OrganisationId org, UserId actor, AttendanceInput input, Checked check, string method, DateTimeOffset recordedAt, string? device, Guid? clientRecordId, CancellationToken ct) => new()
    {
        Id = UuidV7.NewId(),
        TenantId = tenant.TenantId.Value,
        OrgId = org.Value,
        ListingId = check.ListingId,
        EnrollmentId = input.EnrollmentId,
        OccurrenceStartsAt = check.StartsAt,
        State = input.State,
        MinutesAttended = input.MinutesAttended,
        CheckedInAt = input.CheckedInAt,
        CheckedOutAt = input.CheckedOutAt,
        Method = method,
        RecordedBy = actor.Value,
        RecordedAt = recordedAt,
        DeviceId = device,
        ClientRecordId = clientRecordId,
        SyncedAt = device is null ? null : clock.StoredNow(),
        NoteEnc = await cipher.EncryptAsync(tenant.TenantId, Blank(input.Note), ct).ConfigureAwait(false),
    };

    private async Task<AttendanceView> ViewAsync(AttendanceRecordRecord r, CancellationToken ct) => new(
        r.Id, r.EnrollmentId, r.ListingId, r.OccurrenceStartsAt, r.State, r.MinutesAttended, r.CheckedInAt, r.CheckedOutAt, r.Method, r.RecordedAt,
        await cipher.DecryptAsync(tenant.TenantId, r.NoteEnc, ct).ConfigureAwait(false), r.SupersededBy);

    private Task AuditAsync(string action, OrganisationId org, IReadOnlyCollection<Guid> enrollments, CancellationToken ct, IReadOnlyDictionary<string, string>? detail = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "enrollment",
            SubjectIds = enrollments,
            ActorOrgId = org,
            Detail = detail ?? new Dictionary<string, string>(),
        }, ct);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Error NoMeeting() => Error.Validation("attendance.occurrence", "A meeting of this activity that hasn't been cancelled.");
}

/// <summary>Registration of the Attendance module.</summary>
public static class AttendanceModule
{
    /// <summary>The request-side services. The host registers <see cref="AttendanceDbContext"/> and field encryption.</summary>
    public static IServiceCollection AddAttendance(this IServiceCollection services)
    {
        services.AddScoped<AttendanceRecords>();
        services.AddScoped<CheckIns>();
        services.AddScoped<KioskDevices>();
        return services;
    }
}
