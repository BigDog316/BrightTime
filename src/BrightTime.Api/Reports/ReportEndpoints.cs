using System.Globalization;
using System.Security.Claims;
using BrightTime.Abstractions.Reporting;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Localisation;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Attendance.Reports;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Reporting;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Reports;

public sealed record ReportRunRequest(string? ReportKey, Guid? ListingId, DateTimeOffset? From, DateTimeOffset? To, string? Format);

public sealed record ReportCatalogueEntry(string Key, string Title, bool NeedsListing, bool NeedsPeriod, bool NamesPeople, int? MaxDays, IReadOnlyList<string> Formats);

/// <summary>
/// An organisation's reports (M3b, D57): roster, attendance and participation, as PDF, Excel or
/// CSV. Each run is made when it is asked for and goes straight back. Nothing rendered is stored,
/// so no copy of a roster is left waiting in a bucket.
/// </summary>
public static class ReportEndpoints
{
    public const string RunAction = "reports.run";

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}/reports").RequireAuthorization();
        org.MapGet("/catalogue", CatalogueAsync);
        org.MapPost("/runs", RunAsync).WithMetadata(new YouthRecordAttribute(RunAction));
        return api;
    }

    private static async Task<IResult> CatalogueAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, Text text, CancellationToken ct)
    {
        if (!await CanAsync(user, orgId, Permissions.Reports.RunProvider, authorization))
        {
            return ApiProblems.From(Forbidden());
        }

        bool rosters = await CanAsync(user, orgId, Permissions.Attendance.ViewRoster, authorization);
        await text.LoadAsync(ct);
        return Results.Ok(ProviderReportKeys.All
            .Where(k => rosters || !ProviderReportKeys.NamesPeople(k))
            .Select(k => new ReportCatalogueEntry(k, text["report." + k], k != ProviderReportKeys.Participation, k != ProviderReportKeys.Roster,
                ProviderReportKeys.NamesPeople(k),
                k switch { ProviderReportKeys.Attendance => AttendanceRecords.MaxRangeDays, ProviderReportKeys.Participation => ProviderReports.MaxParticipationDays, _ => null },
                ReportFormats.All))
            .ToList());
    }

    /// <summary>Runs a report and returns the file. A report that names people also needs the roster permission.</summary>
    private static async Task<IResult> RunAsync(Guid orgId, ReportRunRequest body, ClaimsPrincipal user, IAuthorizationService authorization,
        ProviderReports reports, AuditTrail audit, Text text, IClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        string key = body.ReportKey ?? string.Empty;
        if (!ProviderReportKeys.All.Contains(key))
        {
            return ApiProblems.From(Error.Validation("reports.unknown", "One of the reports the catalogue lists."));
        }

        string action = ProviderReports.AuditAction(key);
        if (!await CanAsync(user, orgId, Permissions.Reports.RunProvider, authorization)
            || (ProviderReportKeys.NamesPeople(key) && !await CanAsync(user, orgId, Permissions.Attendance.ViewRoster, authorization)))
        {
            return Refused(audit, action, org, Forbidden());
        }

        string format = (body.Format ?? ReportFormats.Pdf).ToLowerInvariant();
        if (!ReportFormats.All.Contains(format))
        {
            return ApiProblems.From(Error.Validation("reports.format", "pdf, xlsx or csv."));
        }

        if (ProviderReportKeys.NamesPeople(key) && body.ListingId is null)
        {
            return ApiProblems.From(Error.Validation("reports.listing", "Choose an activity."));
        }

        UserId actor = new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
        DateTimeOffset to = body.To ?? clock.UtcNow;
        await text.LoadAsync(ct);
        Result<ReportDocument> made = key switch
        {
            ProviderReportKeys.Roster => Word(await reports.RosterAsync(org, actor, body.ListingId!.Value, format, ct), r => Roster(r, text)),
            ProviderReportKeys.Attendance => Word(await reports.AttendanceAsync(org, actor, body.ListingId!.Value, body.From ?? to.AddDays(-28), to, format, ct), r => Attendance(r, text)),
            _ => Word(await reports.ParticipationAsync(org, actor, body.From ?? to.AddDays(-90), to, format, ct), r => Participation(r, text)),
        };
        if (!made.IsSuccess)
        {
            return Refused(audit, action, org, made.Error);
        }

        ReportDocument report = made.Value with
        {
            RightToLeft = text.Culture.TextInfo.IsRightToLeft,
            FileName = $"{key.Replace('.', '-')}-{clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        };
        return new ReportFile(ReportRenderer.Render(report, format), ReportFormats.ContentType(format), $"{report.FileName}.{format}");
    }

    // ---- wording -----------------------------------------------------------------------------

    private static Result<ReportDocument> Word<T>(Result<T> data, Func<T, ReportDocument> word) =>
        data.IsSuccess ? Result<ReportDocument>.Success(word(data.Value)) : Result<ReportDocument>.Failure(data.Error);

    private static ReportDocument Roster(RosterReport r, Text text)
    {
        List<IReadOnlyList<string>> rows = [.. r.Lines.Select(l => (IReadOnlyList<string>)
            [l.DisplayName ?? text["report.not_shared"], text.DateShort(l.EnrolledAt), Number(l.Attended), Number(l.Recorded)])];
        return new ReportDocument(text["report." + ProviderReportKeys.Roster], [r.Title, text.Format("report.as_of", ("date", text.Date(r.AsOf)))],
            [new ReportTable(r.Title, [new(text["report.col.name"]), new(text["report.col.enrolled"]), new(text["report.col.attended"], Numeric: true), new(text["report.col.recorded"], Numeric: true)], rows)])
        {
            Notes = rows.Count == 0 ? [text["report.empty"]] : [text["report.roster_note"]],
        };
    }

    private static ReportDocument Attendance(AttendanceReport r, Text text)
    {
        HashSet<DateOnly> days = [];
        HashSet<DateOnly> twice = [.. r.Meetings.Select(m => DateOnly.FromDateTime(m.UtcDateTime)).Where(d => !days.Add(d))];
        List<ReportColumn> columns = [new(text["report.col.name"]), .. r.Meetings.Select(m =>
            new ReportColumn(twice.Contains(DateOnly.FromDateTime(m.UtcDateTime)) ? text.DateTime(m) : text.DateShort(m))), new(text["report.col.attended"], Numeric: true)];
        List<IReadOnlyList<string>> rows = [.. r.Lines.Select(l => (IReadOnlyList<string>)
            [l.DisplayName ?? text["report.not_shared"], .. l.States.Select(s => s is null ? string.Empty : text["report.code." + s]),
             Number(l.States.Count(s => s is AttendanceStates.Present or AttendanceStates.Late or AttendanceStates.LeftEarly))])];
        return new ReportDocument(text["report." + ProviderReportKeys.Attendance], [r.Title, text.Range(text.Date(r.From), text.Date(r.To))],
            [new ReportTable(r.Title, columns, rows)])
        {
            Notes = rows.Count == 0 ? [text["report.empty"]] : [text["report.legend"]],
        };
    }

    private static ReportDocument Participation(ParticipationReport r, Text text)
    {
        string hidden = text.Format("report.hidden_cell", ("k", r.Suppression.MinCellSize));
        string Shown(long? value) => value is { } v ? Number(v) : hidden;
        string Rate(long? attended, long? recorded) =>
            attended is { } a && recorded is { } n && n > 0 ? Math.Round(100m * a / n).ToString(CultureInfo.InvariantCulture) : hidden;
        IReadOnlyList<string> Row(ParticipationRow row, string title) =>
            [title, Number(row.Meetings), Shown(row.YoungPeople), Shown(row.Attendances), Shown(row.Records), Rate(row.Attendances, row.Records)];

        ReportTable byActivity = new(text["report.by_activity"],
            [new(text["report.col.activity"]), new(text["report.col.meetings"], Numeric: true), new(text["report.col.young_people"], Numeric: true),
             new(text["report.col.attendances"], Numeric: true), new(text["report.col.records"], Numeric: true), new(text["report.col.rate"], Numeric: true)],
            [.. r.Listings.Select(l => Row(l, l.Title))])
        {
            Total = Row(r.Total, text["report.total"]),
        };
        ReportTable byAge = new(text["report.by_age"], [new(text["report.col.age"]), new(text["report.col.young_people"], Numeric: true)],
            [.. r.AgeBands.Select(a => (IReadOnlyList<string>)[text["report.age." + (a.AgeBand == AgeBand.Unknown ? "unknown" : a.AgeBand.ToDbValue())], Shown(a.YoungPeople)])])
        {
            Total = [text["report.total"], Shown(r.AgeBandTotal)],
        };
        List<string> notes = [text.Format("report.hidden", ("k", r.Suppression.MinCellSize), ("count", r.Suppression.SuppressedCells))];
        if (r.Listings.Count == 0)
        {
            notes.Insert(0, text["report.empty"]);
        }

        return new ReportDocument(text["report." + ProviderReportKeys.Participation], [text.Range(text.Date(r.From), text.Date(r.To))], [byActivity, byAge])
        {
            Notes = notes,
        };
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- authorisation -----------------------------------------------------------------------

    private static async Task<bool> CanAsync(ClaimsPrincipal user, Guid orgId, string permission, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, OrganisationRoles.Staff))).Succeeded;

    private static Error Forbidden() => Error.Forbidden("reports.forbidden", "Only staff of this organisation run its reports.");

    private static IResult Refused(AuditTrail audit, string action, OrganisationId org, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "enrollment", ActorOrgId = org, Reason = error.Code });
        return ApiProblems.From(error);
    }

    /// <summary>The file, never cached: it can name young people.</summary>
    private sealed class ReportFile(byte[] bytes, string contentType, string fileName) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, contentType, fileName).ExecuteAsync(httpContext);
        }
    }
}
