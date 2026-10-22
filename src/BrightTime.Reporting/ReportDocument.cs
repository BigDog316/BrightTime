namespace BrightTime.Reporting;

/// <summary>A column: its heading, and whether its cells are numbers (right-aligned, and numeric in a spreadsheet).</summary>
public sealed record ReportColumn(string Header, bool Numeric = false);

/// <summary>
/// One table of a report. Cells are already worded in the reader's language; a numeric column's
/// cell that isn't a number (a hidden small count, say) stays text.
/// </summary>
public sealed record ReportTable(string Heading, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>A last row set apart: the table's totals.</summary>
    public IReadOnlyList<string>? Total { get; init; }
}

/// <summary>
/// A report, worded and ready to render (D57): a title, a few lines saying what it covers, its
/// tables, and notes such as what was hidden and why. The renderers lay it out; they decide nothing.
/// </summary>
public sealed record ReportDocument(string Title, IReadOnlyList<string> Lines, IReadOnlyList<ReportTable> Tables)
{
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>For Arabic: the page reads from the right.</summary>
    public bool RightToLeft { get; init; }

    /// <summary>The file's name without an extension, safe on every platform.</summary>
    public string FileName { get; init; } = "report";
}

public static class ReportFormats
{
    public const string Pdf = "pdf";
    public const string Xlsx = "xlsx";
    public const string Csv = "csv";

    public static IReadOnlyList<string> All { get; } = [Pdf, Xlsx, Csv];

    public static string ContentType(string format) => format switch
    {
        Pdf => "application/pdf",
        Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        Csv => "text/csv; charset=utf-8",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a report format."),
    };
}
