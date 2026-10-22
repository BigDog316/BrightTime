using System.Text;
using BrightTime.Abstractions.Reporting;
using BrightTime.Reporting;
using ClosedXML.Excel;

namespace BrightTime.Api.Tests;

/// <summary>
/// Small-cell suppression and the report renderers (D57), with no database: what is hidden, and
/// that a file can't be turned against whoever opens it.
/// </summary>
public sealed class ReportRenderingTests
{
    [Fact]
    public void Small_counts_are_hidden_and_zero_is_published()
    {
        SmallCells.Suppress(0, 10).ShouldBe(0);
        SmallCells.Suppress(1, 10).ShouldBeNull();
        SmallCells.Suppress(9, 10).ShouldBeNull();
        SmallCells.Suppress(10, 10).ShouldBe(10);
    }

    [Fact]
    public void A_lone_hidden_count_takes_the_smallest_other_with_it_so_the_total_gives_nothing_away()
    {
        (long?[] cells, long? total, int hidden) = SmallCells.Column([40, 3, 12, 0, 25], 80, 10);
        cells.ShouldBe([40, null, null, 0, 25]);
        (total, hidden).ShouldBe((80, 2));

        // Two hidden already: neither can be worked out alone, so nothing more goes.
        (cells, _, hidden) = SmallCells.Column([40, 3, 4, 25], 72, 10);
        cells.ShouldBe([40, null, null, 25]);
        hidden.ShouldBe(2);

        // A hidden total protects the line itself.
        (cells, total, hidden) = SmallCells.Column([3, 4], 7, 10);
        cells.ShouldBe([null, null]);
        (total, hidden).ShouldBe((null, 3));

        // Without complementary suppression only the small one goes.
        (cells, _, _) = SmallCells.Column([40, 3, 12], 55, 10, complementary: false);
        cells.ShouldBe([40, null, 12]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1 555", "'+1 555")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-12", "-12")]
    [InlineData("Owusu, Ama", "\"Owusu, Ama\"")]
    [InlineData("<10", "<10")]
    public void A_csv_cell_never_runs_as_a_formula(string value, string written) =>
        ReportRenderer.Field(value).ShouldBe(written);

    private static ReportDocument Sample(bool rtl = false) =>
        new("Participation", ["1 Oct – 21 Oct"],
        [
            new ReportTable("By activity [summer]: robotics/coding club, and more", [new("Activity"), new("Young people", Numeric: true)],
                [["Robotics", "42"], ["=cmd", "<10"], ["حلقة القراءة", "12"]]) { Total = ["Total", "60"] },
            new ReportTable("By activity [summer]: robotics/coding club, and more", [new("Age"), new("Young people", Numeric: true)], [["9 to 12", "30"]]),
        ])
        {
            Notes = ["A count under 10 is shown as “<10”."],
            RightToLeft = rtl,
        };

    [Fact]
    public void A_spreadsheet_keeps_numbers_as_numbers_text_as_text_and_safe_sheet_names()
    {
        ReportRenderer.Configure("Community");
        using XLWorkbook book = new(new MemoryStream(ReportRenderer.Render(Sample(), ReportFormats.Xlsx)));
        book.Worksheets.Select(w => w.Name).ShouldBe(["By activity summer roboticscodi", "By activity summer roboticsco 2"]);
        IXLWorksheet sheet = book.Worksheets.First();
        IXLCell robotics = sheet.CellsUsed().Single(c => c.GetString() == "Robotics");
        robotics.CellRight().DataType.ShouldBe(XLDataType.Number);
        IXLCell formula = sheet.CellsUsed().Single(c => c.GetString() == "=cmd");
        (formula.HasFormula, formula.DataType).ShouldBe((false, XLDataType.Text));
        formula.CellRight().GetString().ShouldBe("<10");
    }

    [Fact]
    public void A_pdf_renders_in_either_direction_and_with_any_script()
    {
        ReportRenderer.Configure("Community");
        foreach (bool rtl in new[] { false, true })
        {
            byte[] pdf = ReportRenderer.Render(Sample(rtl), ReportFormats.Pdf);
            Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        }
    }

    [Fact]
    public void A_csv_starts_with_a_byte_order_mark_so_spreadsheets_read_the_names_right()
    {
        byte[] csv = ReportRenderer.Render(Sample(), ReportFormats.Csv);
        csv[..3].ShouldBe(Encoding.UTF8.GetPreamble());
        string text = Encoding.UTF8.GetString(csv[3..]);
        text.ShouldContain("'=cmd,<10\r\n");
        text.ShouldContain("حلقة القراءة,12");
    }
}
