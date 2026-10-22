using System.Globalization;
using BrightTime.Localisation;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Attendance.Reports;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Reporting;

/// <summary>A report the caller may run: what to ask for, and how long a period it takes.</summary>
public sealed record ReportCatalogueEntry(string Key, string Title, bool NeedsListing, bool NeedsPeriod, bool NamesPeople, int? MaxDays, IReadOnlyList<string> Formats);

/// <summary>A rendered report, ready to send. Never stored (D57).</summary>
public sealed record RenderedReport(byte[] Bytes, string ContentType, string FileName);

/// <summary>
/// Runs a provider report for any host (D57): checks what was asked for, gets the data from
/// <see cref="ProviderReports"/> (which records and audits the run), words it in the reader's
/// language and renders it. The host authorises first: <c>reports.run_provider</c>, plus
/// <c>attendance.view_roster</c> for a report that names people.
/// </summary>
public sealed class ProviderReportRunner(ProviderReports reports, Text text, IClock clock)
{
    /// <summary>The reports the caller may run; those that name people only with the roster permission.</summary>
    public async Task<IReadOnlyList<ReportCatalogueEntry>> CatalogueAsync(bool canNamePeople, CancellationToken cancellationToken = default)
    {
        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. ProviderReportKeys.All
            .Where(k => canNamePeople || !ProviderReportKeys.NamesPeople(k))
            .Select(k => new ReportCatalogueEntry(k, text["report." + k], k != ProviderReportKeys.Participation, k != ProviderReportKeys.Roster,
                ProviderReportKeys.NamesPeople(k),
                k switch { ProviderReportKeys.Attendance => AttendanceRecords.MaxRangeDays, ProviderReportKeys.Participation => ProviderReports.MaxParticipationDays, _ => null },
                ReportFormats.All))];
    }

    /// <summary>Checks the request's shape before anything is read: the report, the format, and a listing where one is needed.</summary>
    public static Error? Check(string? key, Guid? listingId, string? format)
    {
        if (key is null || !ProviderReportKeys.All.Contains(key))
        {
            return Error.Validation("reports.unknown", "One of the reports the catalogue lists.");
        }

        if (!ReportFormats.All.Contains(Format(format)))
        {
            return Error.Validation("reports.format", "pdf, xlsx or csv.");
        }

        return ProviderReportKeys.NamesPeople(key) && listingId is null
            ? Error.Validation("reports.listing", "Choose an activity.")
            : null;
    }

    public static string Format(string? format) => (format ?? ReportFormats.Pdf).ToLowerInvariant();

    /// <summary>
    /// Runs the report. Without a period, attendance covers the last 28 days and participation the
    /// last 90.
    /// </summary>
    public async Task<Result<RenderedReport>> RunAsync(OrganisationId org, UserId actor, string? key, Guid? listingId, DateTimeOffset? from, DateTimeOffset? to, string? format,
        CancellationToken cancellationToken = default)
    {
        if (Check(key, listingId, format) is { } invalid)
        {
            return Result<RenderedReport>.Failure(invalid);
        }

        string report = key ?? string.Empty;
        string chosen = Format(format);
        DateTimeOffset end = to ?? clock.UtcNow;
        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        Result<ReportDocument> made = report switch
        {
            ProviderReportKeys.Roster => Word(await reports.RosterAsync(org, actor, listingId!.Value, chosen, cancellationToken).ConfigureAwait(false), Roster),
            ProviderReportKeys.Attendance => Word(await reports.AttendanceAsync(org, actor, listingId!.Value, from ?? end.AddDays(-28), end, chosen, cancellationToken).ConfigureAwait(false), Attendance),
            _ => Word(await reports.ParticipationAsync(org, actor, from ?? end.AddDays(-90), end, chosen, cancellationToken).ConfigureAwait(false), Participation),
        };
        if (!made.IsSuccess)
        {
            return Result<RenderedReport>.Failure(made.Error);
        }

        ReportDocument document = made.Value with
        {
            RightToLeft = text.Culture.TextInfo.IsRightToLeft,
            FileName = $"{report.Replace('.', '-')}-{clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        };
        return Result<RenderedReport>.Success(new RenderedReport(ReportRenderer.Render(document, chosen), ReportFormats.ContentType(chosen), $"{document.FileName}.{chosen}"));
    }

    // ---- wording -----------------------------------------------------------------------------

    private static Result<ReportDocument> Word<T>(Result<T> data, Func<T, ReportDocument> word) =>
        data.IsSuccess ? Result<ReportDocument>.Success(word(data.Value)) : Result<ReportDocument>.Failure(data.Error);

    private ReportDocument Roster(RosterReport r)
    {
        List<IReadOnlyList<string>> rows = [.. r.Lines.Select(l => (IReadOnlyList<string>)
            [l.DisplayName ?? text["report.not_shared"], text.DateShort(l.EnrolledAt), Number(l.Attended), Number(l.Recorded)])];
        return new ReportDocument(text["report." + ProviderReportKeys.Roster], [r.Title, text.Format("report.as_of", ("date", text.Date(r.AsOf)))],
            [new ReportTable(r.Title, [new(text["report.col.name"]), new(text["report.col.enrolled"]), new(text["report.col.attended"], Numeric: true), new(text["report.col.recorded"], Numeric: true)], rows)])
        {
            Notes = rows.Count == 0 ? [text["report.empty"]] : [text["report.roster_note"]],
        };
    }

    private ReportDocument Attendance(AttendanceReport r)
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

    private ReportDocument Participation(ParticipationReport r)
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
}

public static class ProviderReportRegistration
{
    /// <summary>The runner, and QuestPDF's licence for this deployment (<c>Reports:QuestPdfLicence</c>, D57). The host registers the Attendance module.</summary>
    public static IServiceCollection AddProviderReports(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ReportRenderer.Configure(configuration["Reports:QuestPdfLicence"]);
        services.AddScoped<ProviderReportRunner>();
        return services;
    }
}
