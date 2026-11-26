using System.Text;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Reporting;
using BrightTime.Reporting;
using BrightTime.SharedKernel.Tests;
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

    /// <summary>Projects as a portfolio has them (D107): one with three pictures, one with none.</summary>
    private static ReportDocument WithPictures(bool rtl = false) =>
        new("Ayo Builds", ["Rivers and robots"], [])
        {
            Groups =
            [
                new ReportGroup("Projects",
                [
                    new ReportEntry("Mapping the mussel beds", ["River Rangers · Kettle Nature Trust"],
                    [
                        new ReportPicture(TestImages.Jpeg(1000, 750), "A hand-drawn map of the river bend", "The map, with its key"),
                        new ReportPicture(TestImages.Jpeg(750, 1000), "Mussels in a bucket", "Mussels in a bucket"),
                        new ReportPicture(TestImages.Png(900, 600), "Our group by the water", "Our group by the water"),
                    ]),
                    new ReportEntry("A poem about rain", [], []),
                ]),
                new ReportGroup("Nothing here", []),
            ],
            RightToLeft = rtl,
        };

    [Fact]
    public void A_pdf_shows_each_entry_with_its_pictures_and_a_spreadsheet_lists_the_entries()
    {
        ReportRenderer.Configure("Community");
        foreach (bool rtl in new[] { false, true })
        {
            byte[] pdf = ReportRenderer.Render(WithPictures(rtl), ReportFormats.Pdf);
            Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
            Regex.Count(Encoding.Latin1.GetString(pdf), @"/Subtype\s*/Image").ShouldBe(3, "each picture, once");
        }

        string csv = Encoding.UTF8.GetString(ReportRenderer.Render(WithPictures(), ReportFormats.Csv)[3..]);
        csv.ShouldContain("Projects\r\nProjects,\r\nMapping the mussel beds,River Rangers · Kettle Nature Trust\r\nA poem about rain,\r\n");
        csv.ShouldNotContain("Nothing here", Case.Sensitive, "a group with nothing in it is left out, as in the PDF");

        using XLWorkbook book = new(new MemoryStream(ReportRenderer.Render(WithPictures(), ReportFormats.Xlsx)));
        book.Worksheets.Select(w => w.Name).ShouldBe(["Projects"]);
        book.Worksheets.First().CellsUsed().Select(c => c.GetString()).ShouldContain("A poem about rain");
    }

    /// <summary>How many structure elements of a kind a PDF has (its structure tree isn't compressed).</summary>
    internal static int Tags(string pdf, string kind) => Regex.Count(pdf, $@"/S\s*/{kind}\b");

    [Fact]
    public void A_pdf_is_tagged_so_a_screen_reader_can_read_it()
    {
        ReportRenderer.Configure("Community");
        string pdf = Encoding.Latin1.GetString(ReportRenderer.Render(WithPictures(), ReportFormats.Pdf));
        pdf.ShouldContain("pdfuaid:part>1<", Case.Sensitive, "it says it's PDF/UA-1");
        pdf.ShouldContain("/Lang (en)");
        pdf.ShouldContain("/DisplayDocTitle true");
        (Tags(pdf, "H1"), Tags(pdf, "H2"), Tags(pdf, "H3")).ShouldBe((1, 1, 2), "the title, the group, each entry");
        Tags(pdf, "Figure").ShouldBe(3);
        foreach (string alt in new[] { "A hand-drawn map of the river bend", "Mussels in a bucket", "Our group by the water" })
        {
            pdf.ShouldContain($"/Alt ({alt})");
        }

        Tags(pdf, "Caption").ShouldBe(1, "a caption that only repeats what the picture shows isn't read a second time");

        // A table's header cells are headers of their columns; a long one runs on, and the title
        // printed again atop each later page isn't another heading.
        ReportDocument tall = Sample() with
        {
            Tables = [new ReportTable("Everyone", [new("Name"), new("Hours", Numeric: true)], [.. Enumerable.Range(1, 200).Select(i => (IReadOnlyList<string>)[$"Person {i}", "3"])])],
            Language = "ar",
            RightToLeft = true,
        };
        string table = Encoding.Latin1.GetString(ReportRenderer.Render(tall, ReportFormats.Pdf));
        Regex.Count(table, @"/Type\s*/Page\b").ShouldBeGreaterThan(1);
        table.ShouldContain("/Lang (ar)");
        (Tags(table, "H1"), Tags(table, "Table"), Tags(table, "TH"), Tags(table, "TD")).ShouldBe((1, 1, 2, 400));
        Regex.Count(table, @"/Scope\s*/Column").ShouldBe(2);
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
