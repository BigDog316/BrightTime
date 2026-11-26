using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BrightTime.Reporting;

/// <summary>
/// Renders a <see cref="ReportDocument"/> as PDF (QuestPDF), Excel (ClosedXML) or CSV (D57).
/// Everything happens in memory and nothing is written to disk: a report holds young people's
/// names, and the bytes go only to whoever asked. A PDF is tagged to PDF/UA-1 (D108): its
/// language and title, headings, tables with their header cells, and each picture with what it
/// shows, so a screen reader can read it; page numbers and the running title are left out of it.
/// </summary>
public static class ReportRenderer
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>The most a picture stands on the page, in points (D107): about three inches.</summary>
    private const float PictureHeight = 210;

    /// <summary>
    /// Sets QuestPDF's licence for this deployment (<c>Community</c>, <c>Professional</c> or
    /// <c>Enterprise</c>; the Community licence has a revenue limit, see D57). A name in a script no
    /// installed font covers is drawn with a fallback rather than failing the whole report.
    /// </summary>
    public static void Configure(string? licence)
    {
        QuestPDF.Settings.License = Enum.TryParse(licence, ignoreCase: true, out LicenseType chosen) ? chosen : LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    public static byte[] Render(ReportDocument report, string format)
    {
        ArgumentNullException.ThrowIfNull(report);
        return format switch
        {
            ReportFormats.Pdf => Pdf(report),
            ReportFormats.Xlsx => Xlsx(report),
            ReportFormats.Csv => Csv(report),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a report format."),
        };
    }

    // ---- PDF ---------------------------------------------------------------------------------

    private static byte[] Pdf(ReportDocument report)
    {
        bool wide = report.Tables.Any(t => t.Columns.Count > 7);
        return Document.Create(container => container.Page(page =>
        {
            page.Size(wide ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9));
            if (report.RightToLeft)
            {
                page.ContentFromRightToLeft();
            }

            // From the second page, the title and what it covers again as a running head: not read out a second time.
            page.Header().SkipOnce().SemanticIgnore().PaddingBottom(8).Text(string.Join(" · ", [report.Title, .. report.Lines])).FontColor(Colors.Grey.Darken2);

            page.Content().Column(body =>
            {
                body.Spacing(14);
                body.Item().Column(title =>
                {
                    title.Item().SemanticHeading1().Text(report.Title).FontSize(16).SemiBold();
                    foreach (string line in report.Lines)
                    {
                        title.Item().SemanticParagraph().Text(line).FontColor(Colors.Grey.Darken2);
                    }
                });

                foreach (ReportTable table in report.Tables)
                {
                    body.Item().SemanticSection().Column(section =>
                    {
                        section.Item().PaddingBottom(4).SemanticHeading2().Text(table.Heading).FontSize(11).SemiBold();
                        section.Item().SemanticTable().Element(e => PdfTable(e, table));
                    });
                }

                foreach (ReportGroup group in report.Groups.Where(g => g.Entries.Count > 0))
                {
                    body.Item().SemanticSection().Column(section =>
                    {
                        section.Spacing(10);
                        section.Item().SemanticHeading2().Text(group.Heading).FontSize(11).SemiBold();
                        foreach (ReportEntry entry in group.Entries)
                        {
                            // A title isn't left alone at the foot of a page.
                            section.Item().EnsureSpace(entry.Pictures.Count > 0 ? 160 : 40).SemanticSection().Element(e => PdfEntry(e, entry));
                        }
                    });
                }

                foreach (string note in report.Notes)
                {
                    body.Item().SemanticParagraph().Text(note).FontColor(Colors.Grey.Darken2);
                }
            });

            page.Footer().SemanticIgnore().AlignCenter().Text(t =>
            {
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        }))
        .WithMetadata(new DocumentMetadata { Title = report.Title, Language = report.Language })
        .WithSettings(new DocumentSettings { PDFUA_Conformance = PDFUA_Conformance.PDFUA_1 })
        .GeneratePdf();
    }

    /// <summary>
    /// An entry: its title and lines, then its pictures two to a row, each kept whole with its
    /// caption. A screen reader reads what a picture shows, then its caption; a caption that only
    /// repeats what it shows is printed but not read again (D108).
    /// </summary>
    private static void PdfEntry(IContainer container, ReportEntry entry) => container.Column(column =>
    {
        column.Item().SemanticHeading3().Text(entry.Title).FontSize(10).SemiBold();
        foreach (string line in entry.Lines)
        {
            column.Item().SemanticParagraph().Text(line).FontColor(Colors.Grey.Darken2);
        }

        if (entry.Pictures.Count == 0)
        {
            return;
        }

        column.Item().PaddingTop(4).Table(t =>
        {
            t.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn();
                columns.RelativeColumn();
            });

            foreach (ReportPicture picture in entry.Pictures)
            {
                t.Cell().Padding(4).ShowEntire().Column(cell =>
                {
                    cell.Item().MaxHeight(PictureHeight).AlignCenter().SemanticFigure(picture.Alt).Image(picture.Image).FitArea();
                    IContainer caption = cell.Item().PaddingTop(3);
                    caption = string.Equals(picture.Caption, picture.Alt, StringComparison.Ordinal) ? caption.SemanticIgnore() : caption.SemanticCaption();
                    caption.Text(picture.Caption).FontSize(8).FontColor(Colors.Grey.Darken2);
                });
            }
        });
    });

    private static void PdfTable(IContainer container, ReportTable table) => container.Table(t =>
    {
        t.ColumnsDefinition(columns =>
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (i == 0)
                {
                    columns.RelativeColumn(3);
                }
                else
                {
                    columns.RelativeColumn();
                }
            }
        });

        t.Header(header =>
        {
            foreach (ReportColumn column in table.Columns)
            {
                header.Cell().BorderBottom(1).BorderColor(Colors.Grey.Darken1).PaddingVertical(3).PaddingHorizontal(2)
                    .Element(c => column.Numeric ? c.AlignRight() : c).Text(column.Header).SemiBold();
            }
        });

        foreach (IReadOnlyList<string> row in table.Rows)
        {
            Cells(t, table, row, bold: false);
        }

        if (table.Total is { } total)
        {
            Cells(t, table, total, bold: true);
        }
    });

    private static void Cells(TableDescriptor t, ReportTable table, IReadOnlyList<string> row, bool bold)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            string value = i < row.Count ? row[i] : string.Empty;
            IContainer cell = t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(2).PaddingHorizontal(2);
            TextBlockDescriptor text = (table.Columns[i].Numeric ? cell.AlignRight() : cell).Text(value);
            if (bold)
            {
                text.SemiBold();
            }
        }
    }

    // ---- Excel -------------------------------------------------------------------------------

    /// <summary>
    /// The tables, then each group with entries as one: a row per entry of its title and lines, as
    /// many cells in each, and no pictures. A group with none is left out, as in a PDF.
    /// </summary>
    private static IEnumerable<ReportTable> AllTables(ReportDocument report) =>
        report.Tables.Concat(report.Groups.Where(g => g.Entries.Count > 0).Select(g =>
        {
            int width = 1 + g.Entries.Max(e => e.Lines.Count);
            return new ReportTable(
                g.Heading,
                [.. Enumerable.Range(0, width).Select(i => new ReportColumn(i == 0 ? g.Heading : string.Empty))],
                [.. g.Entries.Select(e => (IReadOnlyList<string>)[e.Title, .. e.Lines, .. Enumerable.Repeat(string.Empty, width - 1 - e.Lines.Count)])]);
        }));

    private static byte[] Xlsx(ReportDocument report)
    {
        using XLWorkbook book = new();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReportTable table in AllTables(report).DefaultIfEmpty(new ReportTable(report.Title, [], [])))
        {
            IXLWorksheet sheet = book.Worksheets.Add(SheetName(table.Heading, names));
            sheet.RightToLeft = report.RightToLeft;
            int row = 1;
            sheet.Cell(row, 1).SetValue(report.Title).Style.Font.SetBold().Font.SetFontSize(14);
            foreach (string line in report.Lines)
            {
                sheet.Cell(++row, 1).SetValue(line);
            }

            row += 2;
            int headerRow = row;
            for (int c = 0; c < table.Columns.Count; c++)
            {
                sheet.Cell(row, c + 1).SetValue(table.Columns[c].Header).Style.Font.SetBold();
            }

            foreach (IReadOnlyList<string> cells in table.Rows)
            {
                XlsxRow(sheet, ++row, table, cells);
            }

            if (table.Total is { } total)
            {
                XlsxRow(sheet, ++row, table, total);
                sheet.Row(row).Style.Font.SetBold();
            }

            foreach (string note in report.Notes)
            {
                sheet.Cell(row += 2, 1).SetValue(note);
            }

            if (table.Columns.Count > 0)
            {
                sheet.SheetView.FreezeRows(headerRow);
                sheet.Columns(1, table.Columns.Count).AdjustToContents(headerRow, row, 8, 60);
            }
        }

        using MemoryStream stream = new();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void XlsxRow(IXLWorksheet sheet, int row, ReportTable table, IReadOnlyList<string> cells)
    {
        for (int c = 0; c < table.Columns.Count; c++)
        {
            string value = c < cells.Count ? cells[c] : string.Empty;
            IXLCell cell = sheet.Cell(row, c + 1);
            if (table.Columns[c].Numeric && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
            {
                cell.SetValue(number);
            }
            else
            {
                // Text, never a formula: SetValue stores a string as a string.
                cell.SetValue(value);
                if (table.Columns[c].Numeric)
                {
                    cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
                }
            }
        }
    }

    /// <summary>Excel's rules: at most 31 characters, none of <c>[]:*?/\</c>, unique in the book.</summary>
    private static string SheetName(string heading, HashSet<string> taken)
    {
        string clean = new([.. heading.Where(ch => "[]:*?/\\".IndexOf(ch, StringComparison.Ordinal) < 0)]);
        clean = clean.Trim().Trim('\'');
        if (clean.Length == 0)
        {
            clean = "Sheet";
        }

        string name = clean.Length > 31 ? clean[..31] : clean;
        for (int n = 2; !taken.Add(name); n++)
        {
            string suffix = " " + n.ToString(CultureInfo.InvariantCulture);
            name = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }

        return name;
    }

    // ---- CSV ---------------------------------------------------------------------------------

    /// <summary>RFC 4180, UTF-8 with a byte-order mark (so Excel reads the names right); tables one after another.</summary>
    private static byte[] Csv(ReportDocument report)
    {
        StringBuilder csv = new();
        Line(csv, [report.Title]);
        foreach (string line in report.Lines)
        {
            Line(csv, [line]);
        }

        foreach (ReportTable table in AllTables(report))
        {
            csv.Append("\r\n");
            Line(csv, [table.Heading]);
            Line(csv, [.. table.Columns.Select(c => c.Header)]);
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                Line(csv, row);
            }

            if (table.Total is { } total)
            {
                Line(csv, total);
            }
        }

        foreach (string note in report.Notes)
        {
            csv.Append("\r\n");
            Line(csv, [note]);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static void Line(StringBuilder csv, IReadOnlyList<string> cells)
    {
        csv.AppendJoin(',', cells.Select(Field));
        csv.Append("\r\n");
    }

    /// <summary>
    /// Quotes as RFC 4180 needs, and defuses a cell a spreadsheet would run as a formula (a name
    /// can start with "="), by starting it with an apostrophe. Plain numbers are left as numbers.
    /// </summary>
    public static string Field(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && FormulaStarts.Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
