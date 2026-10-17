using BrightTime.Modules.Opportunities.Import;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>Reading CSV rows and HSDS services into import records, before any lookup.</summary>
public sealed class ImportFormatTests
{
    private static IReadOnlyList<ImportRecord> Csv(string text)
    {
        (IReadOnlyList<CsvRecord>? table, _) = CsvTable.Read(text);
        (IReadOnlyList<ImportRecord>? records, var problem) = CsvListingFormat.Map(table!);
        problem.ShouldBeNull();
        return records!;
    }

    [Fact]
    public void The_template_reads_back_as_one_complete_row()
    {
        ImportRecord row = Csv(CsvListingFormat.Template()).Single();

        row.Errors.ShouldBeEmpty();
        row.ExternalId.ShouldBe("spring-robotics");
        row.Form.Title.ShouldBe("Robotics Club");
        row.Form.Days.ShouldBe(["TU", "TH"]);
        row.Form.Languages.ShouldBe(["en", "es"]);
        row.SiteNames.ShouldBe(["Main Street Center"]);
        row.CategoryCodes.ShouldBe(["stem"]);
        row.Form.SkipDates.ShouldBe("2027-04-13\n2027-04-15");
    }

    [Fact]
    public void Columns_may_come_in_any_order_and_case_and_most_may_be_left_out()
    {
        ImportRecord row = Csv("Title,ID,Summary,Starts_On,Start_Time,End_Time\nChess,c-1,Learn chess.,2027-01-05,16:00,17:00").Single();

        row.ExternalId.ShouldBe("c-1");
        row.Form.Title.ShouldBe("Chess");
        row.Form.Kind.ShouldBe("program");
        row.Form.Repeats.ShouldBe("weekly");
    }

    [Theory]
    [InlineData("id,title,summary,starts_on,start_time,end_time,colour\n", "import.error.unknown_columns")]
    [InlineData("id,title,summary,starts_on,start_time\n", "import.error.missing_columns")]
    [InlineData("id,id,title,summary,starts_on,start_time,end_time\n", "import.error.duplicate_columns")]
    public void A_header_that_does_not_match_the_format_is_refused_for_the_whole_file(string header, string error)
    {
        (IReadOnlyList<CsvRecord>? table, _) = CsvTable.Read(header + "x,y,z,a,b,c,d\n");

        CsvListingFormat.Map(table!).Problem!.Key.ShouldBe(error);
    }

    [Fact]
    public void A_row_of_the_wrong_width_is_an_error_for_that_row_only()
    {
        IReadOnlyList<ImportRecord> rows = Csv("id,title,summary,starts_on,start_time,end_time\na,A,S,2027-01-05,16:00,17:00\nb,B\n");

        rows[0].Errors.ShouldBeEmpty();
        rows[1].Errors["*"].Single().Key.ShouldBe("import.error.wrong_width");
    }

    [Fact]
    public void A_yes_no_column_refuses_anything_else()
    {
        ImportRecord row = Csv("id,title,summary,starts_on,start_time,end_time,meals\na,A,S,2027-01-05,16:00,17:00,sometimes\n").Single();

        row.Errors["meals"].Single().Key.ShouldBe("import.error.yes_no");
    }

    private const string Service =
        """
        {
          "id": "svc-17",
          "name": "Homework Help",
          "description": "Free tutoring after school. Drop in any weekday.",
          "status": "active",
          "url": "https://example.org/homework",
          "email": "help@example.org",
          "minimum_age": 9.5,
          "maximum_age": 14,
          "phones": [{ "number": "555-0100" }],
          "languages": [{ "name": "Spanish", "code": "ES" }],
          "service_at_locations": [{ "location": { "name": "Library Annex" } }],
          "attributes": [{ "taxonomy_term": { "code": "tutoring" } }, { "taxonomy_term": { "code": "211-xyz" } }],
          "cost_options": [{ "amount": 0 }],
          "schedules": [{ "freq": "WEEKLY", "interval": 1, "byday": "MO,WE", "dtstart": "2027-01-04", "until": "2027-06-16", "opens_at": "15:00:00", "closes_at": "17:00:00" }]
        }
        """;

    [Fact]
    public void An_hsds_service_maps_to_a_program()
    {
        (IReadOnlyList<ImportRecord>? records, var problem) = HsdsListingFormat.Map($"{{\"services\": [{Service}]}}");

        problem.ShouldBeNull();
        ImportRecord row = records!.Single();
        row.Errors.ShouldBeEmpty();
        row.ExternalId.ShouldBe("svc-17");
        row.Form.Title.ShouldBe("Homework Help");
        row.Form.Summary.ShouldBe("Free tutoring after school.");
        row.Form.AgeMin.ShouldBe("10");
        row.Form.AgeMax.ShouldBe("14");
        row.Form.CostModel.ShouldBe("free");
        row.Form.RegistrationMode.ShouldBe("external");
        row.Form.Languages.ShouldBe(["es"]);
        row.Form.Days.ShouldBe(["MO", "WE"]);
        row.Form.StartsOn.ShouldBe("2027-01-04");
        row.Form.EndsOn.ShouldBe("2027-06-16");
        row.Form.StartTime.ShouldBe("15:00");
        row.SiteNames.ShouldBe(["Library Annex"]);
        row.CategoryCodes.ShouldBe(["tutoring", "211-xyz"]);
    }

    [Fact]
    public void A_bare_array_and_an_api_page_are_read_too()
    {
        HsdsListingFormat.Map($"[{Service}]").Records!.Count.ShouldBe(1);
        HsdsListingFormat.Map($"{{\"contents\": [{Service}], \"total_items\": 1}}").Records!.Count.ShouldBe(1);
    }

    [Fact]
    public void A_monthly_schedule_is_refused_rather_than_guessed()
    {
        string monthly = Service.Replace("\"freq\": \"WEEKLY\"", "\"freq\": \"MONTHLY\"", StringComparison.Ordinal).Replace("\"MO,WE\"", "\"1MO\"", StringComparison.Ordinal);

        HsdsListingFormat.Map($"[{monthly}]").Records!.Single().Errors["schedules"].Single().Key.ShouldBe("import.error.hsds_schedule");
    }

    [Fact]
    public void An_inactive_service_is_reported()
    {
        string inactive = Service.Replace("\"active\"", "\"defunct\"", StringComparison.Ordinal);

        HsdsListingFormat.Map($"[{inactive}]").Records!.Single().Errors["status"].Single().Key.ShouldBe("import.error.hsds_inactive");
    }

    [Theory]
    [InlineData("not json", "import.error.bad_json")]
    [InlineData("{\"name\": \"x\"}", "import.error.not_hsds")]
    [InlineData("[]", "import.error.no_rows")]
    public void A_file_that_is_not_hsds_is_refused(string json, string error) =>
        HsdsListingFormat.Map(json).Problem!.Key.ShouldBe(error);

    [Fact]
    public void A_long_first_sentence_is_shortened_at_a_word()
    {
        string summary = HsdsListingFormat.SummaryOf(string.Join(' ', Enumerable.Repeat("word", 100)), 30);

        summary.Length.ShouldBeLessThanOrEqualTo(30);
        summary.ShouldEndWith("word…");
    }
}
