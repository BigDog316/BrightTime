using System.Text.Json;
using BrightTime.Abstractions.Reporting;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Attendance.Reports;

/// <summary>The provider reports (D57), as <c>analytics.report_run.report_key</c> names them.</summary>
public static class ProviderReportKeys
{
    public const string Roster = "provider.roster";
    public const string Attendance = "provider.attendance";
    public const string Participation = "provider.participation";

    public static IReadOnlyList<string> All { get; } = [Roster, Attendance, Participation];

    /// <summary>Roster and attendance name young people; participation only counts them.</summary>
    public static bool NamesPeople(string key) => key is Roster or Attendance;
}

[YouthData]
public sealed record RosterReportLine(Guid EnrollmentId, string? DisplayName, DateTimeOffset EnrolledAt, int Attended, int Recorded);

[YouthData]
public sealed record RosterReport(Guid ListingId, string Title, DateTimeOffset AsOf, IReadOnlyList<RosterReportLine> Lines);

/// <summary>One young person's states for each of the report's meetings, in order; null where nothing was recorded.</summary>
[YouthData]
public sealed record AttendanceReportLine(Guid EnrollmentId, string? DisplayName, IReadOnlyList<string?> States);

[YouthData]
public sealed record AttendanceReport(Guid ListingId, string Title, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<DateTimeOffset> Meetings, IReadOnlyList<AttendanceReportLine> Lines);

/// <summary>One activity's counts, or the organisation's total; a null count was too small to show.</summary>
public sealed record ParticipationRow(Guid? ListingId, string Title, int Meetings, long? YoungPeople, long? Attendances, long? Records);

public sealed record AgeBandCount(AgeBand AgeBand, long? YoungPeople);

public sealed record ParticipationReport(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ParticipationRow> Listings,
    ParticipationRow Total,
    IReadOnlyList<AgeBandCount> AgeBands,
    long? AgeBandTotal,
    SuppressionSummary Suppression);

/// <summary>
/// An organisation's own reports (M3b, D57): a listing's roster, its attendance over up to
/// <see cref="AttendanceRecords.MaxRangeDays"/> days, and participation across the organisation over
/// up to a year.
/// </summary>
/// <remarks>
/// <para>
/// Roster and attendance name young people, so they follow the attendance rules (docs/plan/07,
/// T4): a name only where the family's live share gives it, enrolments ended more than
/// <see cref="AttendanceRecords.GraceDays"/> days ago left out, staff notes never included, and
/// every run audited with the enrolments it covers.
/// </para>
/// <para>
/// Participation is the report an organisation hands to a funder, so it only counts, and every
/// count of people passes through <see cref="SmallCells"/> at the tenant's threshold
/// (<c>analytics.min_cell</c>), with complementary suppression. Every run is recorded in
/// <c>analytics.report_run</c>. Nothing rendered is kept: the file goes back to whoever asked.
/// </para>
/// </remarks>
public sealed class ProviderReports(
    AttendanceDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IListingDetails listings,
    IListingOccurrences occurrences,
    IEnrolmentQueries enrolments)
{
    public const int MaxParticipationDays = 366;

    /// <summary>States that count as attending: there for at least part of the meeting.</summary>
    private static readonly string[] Attended = [AttendanceStates.Present, AttendanceStates.Late, AttendanceStates.LeftEarly];

    public static string AuditAction(string reportKey) => "reports." + reportKey;

    /// <summary>A listing's current roster, with each young person's attendance since they enrolled.</summary>
    public Task<Result<RosterReport>> RosterAsync(OrganisationId org, UserId actor, Guid listingId, string format, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await TitleAsync(org, listingId, ct).ConfigureAwait(false) is not { } title)
            {
                return Result<RosterReport>.Failure(NoListing());
            }

            DateTimeOffset now = clock.StoredNow();
            IReadOnlyList<RosterEntry> roster = await enrolments.RosterAsync(listingId, ct).ConfigureAwait(false);
            Guid[] ids = [.. roster.Select(r => r.EnrollmentId)];
            Dictionary<Guid, EnrolmentFacts> facts = (await enrolments.FactsAsync(ids, DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false))
                .ToDictionary(f => f.EnrollmentId);
            var counts = await db.Records.AsNoTracking()
                .Where(r => ids.Contains(r.EnrollmentId) && r.SupersededBy == null && r.State != AttendanceStates.Unknown)
                .GroupBy(r => r.EnrollmentId)
                .Select(g => new { Id = g.Key, Recorded = g.Count(), Attended = g.Count(r => Attended.Contains(r.State)) })
                .ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
            List<RosterReportLine> lines = [.. roster.Select(r => new RosterReportLine(
                r.EnrollmentId,
                r.DisplayName,
                facts.TryGetValue(r.EnrollmentId, out EnrolmentFacts? f) ? f.EnrolledAt : now,
                counts.TryGetValue(r.EnrollmentId, out var c) ? c.Attended : 0,
                c?.Recorded ?? 0))];

            await RanAsync(ProviderReportKeys.Roster, org, actor, format, new { listingId }, lines.Count, 0, ids, ct).ConfigureAwait(false);
            return Result<RosterReport>.Success(new RosterReport(listingId, title, now, lines));
        }, cancellationToken);

    /// <summary>A listing's attendance: each young person against each meeting in the window.</summary>
    public Task<Result<AttendanceReport>> AttendanceAsync(OrganisationId org, UserId actor, Guid listingId, DateTimeOffset from, DateTimeOffset to, string format, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            // A period may arrive in any offset; the database takes UTC.
            (from, to) = (from.ToUniversalTime(), to.ToUniversalTime());
            if (to <= from || to - from > TimeSpan.FromDays(AttendanceRecords.MaxRangeDays))
            {
                return Result<AttendanceReport>.Failure(Range(AttendanceRecords.MaxRangeDays));
            }

            if (await TitleAsync(org, listingId, ct).ConfigureAwait(false) is not { } title)
            {
                return Result<AttendanceReport>.Failure(NoListing());
            }

            IReadOnlyList<ListingTime> meetings = await occurrences.GetAsync(listingId, from, to, ct).ConfigureAwait(false);
            List<AttendanceRecordRecord> records = await db.Records.AsNoTracking()
                .Where(r => r.OrgId == org.Value && r.ListingId == listingId && r.SupersededBy == null && r.OccurrenceStartsAt >= from && r.OccurrenceStartsAt < to)
                .ToListAsync(ct).ConfigureAwait(false);

            // Everyone enrolled now, and anyone with a record in the window whose enrolment ended within the grace period.
            IReadOnlyList<RosterEntry> roster = await enrolments.RosterAsync(listingId, ct).ConfigureAwait(false);
            Dictionary<Guid, string?> names = roster.ToDictionary(r => r.EnrollmentId, r => r.DisplayName);
            DateTimeOffset cutoff = clock.StoredNow().AddDays(-AttendanceRecords.GraceDays);
            Guid[] others = [.. records.Select(r => r.EnrollmentId).Distinct().Where(id => !names.ContainsKey(id))];
            foreach (EnrolmentSummary ended in await enrolments.GetAsync(others, ct).ConfigureAwait(false))
            {
                if (ended.OrgId == org.Value && (ended.EndedAt is null || ended.EndedAt > cutoff))
                {
                    names[ended.Id] = null;
                }
            }

            List<DateTimeOffset> starts = [.. meetings.Select(m => m.StartsAt).Union(records.Where(r => names.ContainsKey(r.EnrollmentId)).Select(r => r.OccurrenceStartsAt)).Order()];
            Dictionary<(Guid, DateTimeOffset), string> states = records.ToDictionary(r => (r.EnrollmentId, r.OccurrenceStartsAt), r => r.State);
            List<AttendanceReportLine> lines = [.. names
                .OrderBy(n => n.Value is null ? 1 : 0).ThenBy(n => n.Value, StringComparer.CurrentCulture).ThenBy(n => n.Key)
                .Select(n => new AttendanceReportLine(n.Key, n.Value, [.. starts.Select(s => states.TryGetValue((n.Key, s), out string? state) ? state : null)]))];

            await RanAsync(ProviderReportKeys.Attendance, org, actor, format, new { listingId, from, to }, lines.Count, 0, [.. names.Keys], ct).ConfigureAwait(false);
            return Result<AttendanceReport>.Success(new AttendanceReport(listingId, title, from, to, starts, lines));
        }, cancellationToken);

    /// <summary>The organisation's participation over the window: counts only, small ones hidden.</summary>
    public Task<Result<ParticipationReport>> ParticipationAsync(OrganisationId org, UserId actor, DateTimeOffset from, DateTimeOffset to, string format, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (from, to) = (from.ToUniversalTime(), to.ToUniversalTime());
            if (to <= from || to - from > TimeSpan.FromDays(MaxParticipationDays))
            {
                return Result<ParticipationReport>.Failure(Range(MaxParticipationDays));
            }

            int threshold = await ThresholdAsync(ct).ConfigureAwait(false);
            IReadOnlyList<OrganisationMeeting> meetings = await occurrences.ForOrganisationAsync(org.Value, from, to, ct).ConfigureAwait(false);
            var records = await db.Records.AsNoTracking()
                .Where(r => r.OrgId == org.Value && r.SupersededBy == null && r.OccurrenceStartsAt >= from && r.OccurrenceStartsAt < to && r.State != AttendanceStates.Unknown)
                .Select(r => new { r.ListingId, r.EnrollmentId, r.State })
                .ToListAsync(ct).ConfigureAwait(false);
            Dictionary<Guid, EnrolmentFacts> facts = (await enrolments.FactsAsync([.. records.Select(r => r.EnrollmentId).Distinct()], DateOnly.FromDateTime(to.UtcDateTime), ct).ConfigureAwait(false))
                .ToDictionary(f => f.EnrollmentId);

            // The organisation's activities in the window: those that met, and those with records.
            Dictionary<Guid, string> titles = meetings.GroupBy(m => m.ListingId).ToDictionary(g => g.Key, g => g.First().Title);
            foreach (Guid listing in records.Select(r => r.ListingId).Distinct().Where(l => !titles.ContainsKey(l)))
            {
                titles[listing] = await TitleAsync(org, listing, ct).ConfigureAwait(false) ?? string.Empty;
            }

            List<Guid> order = [.. titles.OrderBy(t => t.Value, StringComparer.CurrentCulture).ThenBy(t => t.Key).Select(t => t.Key)];
            long[] people = [.. order.Select(l => (long)records.Where(r => r.ListingId == l).Select(r => r.EnrollmentId).Distinct().Count())];
            long[] attendances = [.. order.Select(l => (long)records.Count(r => r.ListingId == l && Attended.Contains(r.State)))];
            long[] recorded = [.. order.Select(l => (long)records.Count(r => r.ListingId == l))];

            // A young person in two activities is one young person in the total.
            HashSet<Guid> members = [.. records.Select(r => facts.TryGetValue(r.EnrollmentId, out EnrolmentFacts? f) ? f.FamilyMemberId : r.EnrollmentId)];
            (long?[] p, long? pTotal, int pHidden) = SmallCells.Column(people, members.Count, threshold);
            (long?[] a, long? aTotal, int aHidden) = SmallCells.Column(attendances, attendances.Sum(), threshold);
            (long?[] n, long? nTotal, int nHidden) = SmallCells.Column(recorded, recorded.Sum(), threshold);

            AgeBand[] bands = [.. Enum.GetValues<AgeBand>().Where(b => b != AgeBand.Unknown), AgeBand.Unknown];
            Dictionary<Guid, AgeBand> bandOf = facts.Values.GroupBy(f => f.FamilyMemberId).ToDictionary(g => g.Key, g => g.First().AgeBand);
            long[] byBand = [.. bands.Select(b => (long)members.Count(m => (bandOf.TryGetValue(m, out AgeBand band) ? band : AgeBand.Unknown) == b))];
            (long?[] ages, long? agesTotal, int ageHidden) = SmallCells.Column(byBand, members.Count, threshold);

            List<ParticipationRow> rows = [.. order.Select((l, i) => new ParticipationRow(l, titles[l], meetings.Count(m => m.ListingId == l), p[i], a[i], n[i]))];
            ParticipationRow total = new(null, string.Empty, meetings.Count, pTotal, aTotal, nTotal);
            List<AgeBandCount> ageRows = [.. bands.Select((b, i) => new AgeBandCount(b, ages[i])).Where((row, i) => byBand[i] > 0 || bands[i] != AgeBand.Unknown)];
            SuppressionSummary suppression = new(threshold, pHidden + aHidden + nHidden + ageHidden, Complementary: true, Policy: "participation");

            await RanAsync(ProviderReportKeys.Participation, org, actor, format, new { from, to }, rows.Count, suppression.SuppressedCells, [], ct).ConfigureAwait(false);
            return Result<ParticipationReport>.Success(new ParticipationReport(from, to, rows, total, ageRows, agesTotal, suppression));
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<string?> TitleAsync(OrganisationId org, Guid listingId, CancellationToken ct) =>
        await listings.GetForReviewAsync(tenant.TenantId, listingId, tenant.Locale.Code, ct).ConfigureAwait(false) is { } listing && listing.OrganisationId == org
            ? listing.Title
            : null;

    /// <summary>
    /// The tenant's threshold for counts of people: the larger of its participation and attendance
    /// thresholds, so a figure hidden under one rule isn't shown under the other.
    /// </summary>
    private async Task<int> ThresholdAsync(CancellationToken ct)
    {
        Guid id = tenant.TenantId.Value;
        List<short> found = await db.Database
            .SqlQuery<short>($"select greatest(analytics.min_cell({id}, 'participation'), analytics.min_cell({id}, 'attendance')) as \"Value\"")
            .ToListAsync(ct).ConfigureAwait(false);
        return Math.Max(found.Count > 0 ? found[0] : (short)10, (short)5);
    }

    /// <summary>Records the run, and audits it with the enrolments a named report covered.</summary>
    private async Task RanAsync(string key, OrganisationId org, UserId actor, string format, object scope, int rows, int hidden, IReadOnlyCollection<Guid> enrollments, CancellationToken ct)
    {
        DateTimeOffset now = clock.StoredNow();
        string scopeJson = JsonSerializer.Serialize(scope, JsonOptions);
        Guid run = UuidV7.NewId();
        Guid? requestedBy = actor == UserId.None ? null : actor.Value;
        await db.Database.ExecuteSqlAsync(
            $"""
            insert into analytics.report_run (id, tenant_id, report_key, requested_by, scope, format, state, row_count, suppressed_cell_count, started_at, completed_at)
            values ({run}, {tenant.TenantId.Value}, {key}, {requestedBy}, {scopeJson}::jsonb || jsonb_build_object('orgId', {org.Value}), {format}, 'available', {rows}, {hidden}, {now}, {now})
            """, ct).ConfigureAwait(false);
        await audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = AuditAction(key),
            SubjectKind = "enrollment",
            SubjectIds = enrollments,
            ActorOrgId = org,
            Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["run"] = run.ToString(), ["format"] = format },
        }, ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static Error NoListing() => Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id.");

    private static Error Range(int days) => Error.Validation("reports.range", $"A window of up to {days} days.");
}
