using BrightTime.Modules.Opportunities.Import;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class CsvTableTests
{
    [Fact]
    public void Plain_fields_split_on_commas_and_lines()
    {
        (IReadOnlyList<CsvRecord>? records, int? bad) = CsvTable.Read("a,b,c\r\n1,2,3\n");

        bad.ShouldBeNull();
        records!.Select(r => string.Join('|', r.Fields)).ShouldBe(["a|b|c", "1|2|3"]);
        records!.Select(r => r.Line).ShouldBe([1, 2]);
    }

    [Fact]
    public void Quoted_fields_keep_commas_quotes_and_line_breaks()
    {
        (IReadOnlyList<CsvRecord>? records, _) = CsvTable.Read("id,summary\n1,\"Fun, free \"\"maker\"\" club\nwith snacks\"\n2,next");

        records![1].Fields[1].ShouldBe("Fun, free \"maker\" club\nwith snacks");
        records[2].Line.ShouldBe(4);
        records[2].Fields[1].ShouldBe("next");
    }

    [Fact]
    public void A_byte_order_mark_and_blank_lines_are_ignored()
    {
        (IReadOnlyList<CsvRecord>? records, _) = CsvTable.Read("﻿id,title\n\n1,A\n,\n");

        records!.Count.ShouldBe(2);
        records[0].Fields[0].ShouldBe("id");
    }

    [Fact]
    public void Empty_fields_are_kept_in_place()
    {
        (IReadOnlyList<CsvRecord>? records, _) = CsvTable.Read("a,,c,\n");

        records!.Single().Fields.ShouldBe(["a", "", "c", ""]);
    }

    [Theory]
    [InlineData("id,title\n1,\"unclosed\n2,x", 2)]
    [InlineData("id,title\n1,ab\"c\"", 2)]
    [InlineData("id,title\n1,\"done\"x", 2)]
    public void Broken_quoting_is_refused_with_its_line(string text, int line)
    {
        (IReadOnlyList<CsvRecord>? records, int? bad) = CsvTable.Read(text);

        records.ShouldBeNull();
        bad.ShouldBe(line);
    }
}
