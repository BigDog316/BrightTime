using BrightTime.Modules.Opportunities.Editing;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Import;

/// <summary>
/// The CSV import's columns. The header row names them, in any order and any letter case;
/// only <see cref="Required"/> must be present. A list column takes values separated by
/// semicolons; a yes/no column takes yes, no, or empty for no.
/// </summary>
public static class CsvListingFormat
{
    /// <summary>A column, and how its text lands on the record.</summary>
    public sealed record Column(string Name, Action<ImportRecord, string> Apply);

    public static readonly IReadOnlyList<string> Required = ["id", "title", "summary", "starts_on", "start_time", "end_time"];

    public static readonly IReadOnlyList<Column> Columns =
    [
        new("id", (_, _) => { }),
        new("kind", (r, v) => r.Form.Kind = Or(v, "program")),
        new("title", (r, v) => r.Form.Title = v),
        new("summary", (r, v) => r.Form.Summary = v),
        new("description", (r, v) => r.Form.Description = v),
        new("language", (r, v) => r.Form.PrimaryLanguage = Or(v, "en")),
        new("age_min", (r, v) => r.Form.AgeMin = v),
        new("age_max", (r, v) => r.Form.AgeMax = v),
        new("grade_min", (r, v) => r.Form.GradeMin = v),
        new("grade_max", (r, v) => r.Form.GradeMax = v),
        new("categories", (r, v) => AddAll(r.CategoryCodes, List(v))),
        new("interests", (r, v) => AddAll(r.InterestCodes, List(v))),
        new("accommodations", (r, v) => AddAll(r.AccommodationCodes, List(v))),
        new("languages", (r, v) => AddAll(r.Form.Languages, List(v))),
        new("modality", (r, v) => r.Form.Modality = Or(v, "in_person")),
        new("virtual_url", (r, v) => r.Form.VirtualUrl = v),
        new("sites", (r, v) => AddAll(r.SiteNames, List(v))),
        new("cost_model", (r, v) => r.Form.CostModel = Or(v, "free")),
        new("cost_min", (r, v) => r.Form.CostMin = v),
        new("cost_max", (r, v) => r.Form.CostMax = v),
        new("sliding_scale_note", (r, v) => r.Form.SlidingScaleNote = v),
        new("registration", (r, v) => r.Form.RegistrationMode = Or(v, "external")),
        new("registration_url", (r, v) => r.Form.ExternalUrl = v),
        new("capacity", (r, v) => r.Form.Capacity = v),
        new("meals", (r, v) => r.Form.MealsProvided = YesNo(r, "meals", v)),
        new("transport", (r, v) => r.Form.TransportProvided = YesNo(r, "transport", v)),
        new("transport_note", (r, v) => r.Form.TransportNote = v),
        new("drop_in", (r, v) => r.Form.IsDropIn = YesNo(r, "drop_in", v)),
        new("accessibility_note", (r, v) => r.Form.AccessibilityNote = v),
        new("contact_email", (r, v) => r.Form.ContactEmail = v),
        new("contact_phone", (r, v) => r.Form.ContactPhone = v),
        new("repeats", (r, v) => r.Form.Repeats = Or(v, "weekly")),
        new("days", (r, v) => AddAll(r.Form.Days, List(v).Select(d => d.ToUpperInvariant()))),
        new("every_weeks", (r, v) => r.Form.Interval = Or(v, "1")),
        new("starts_on", (r, v) => r.Form.StartsOn = v),
        new("ends_on", (r, v) => r.Form.EndsOn = v),
        new("start_time", (r, v) => r.Form.StartTime = v),
        new("end_time", (r, v) => r.Form.EndTime = v),
        new("skip_dates", (r, v) => r.Form.SkipDates = string.Join('\n', List(v))),
        new("deadline_kind", (r, v) => r.Form.DeadlineKind = v),
        new("deadline_on", (r, v) => r.Form.DeadlineOn = v),
        new("show_from", (r, v) => r.Form.ShowFrom = v),
        new("take_down_after", (r, v) => r.Form.TakeDownAfter = v),
    ];

    /// <summary>A file's column names for an editor field, so a parse error points at the column.</summary>
    public static readonly IReadOnlyDictionary<string, string> ColumnForField = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(ListingForm.Kind)] = "kind",
        [nameof(ListingForm.Title)] = "title",
        [nameof(ListingForm.Summary)] = "summary",
        [nameof(ListingForm.Description)] = "description",
        [nameof(ListingForm.PrimaryLanguage)] = "language",
        [nameof(ListingForm.AgeMin)] = "age_min",
        [nameof(ListingForm.AgeMax)] = "age_max",
        [nameof(ListingForm.GradeMin)] = "grade_min",
        [nameof(ListingForm.GradeMax)] = "grade_max",
        [nameof(ListingForm.TermIds)] = "categories",
        [nameof(ListingForm.AccommodationIds)] = "accommodations",
        [nameof(ListingForm.Languages)] = "languages",
        [nameof(ListingForm.Modality)] = "modality",
        [nameof(ListingForm.VirtualUrl)] = "virtual_url",
        [nameof(ListingForm.SiteIds)] = "sites",
        [nameof(ListingForm.CostModel)] = "cost_model",
        [nameof(ListingForm.CostMin)] = "cost_min",
        [nameof(ListingForm.CostMax)] = "cost_max",
        [nameof(ListingForm.SlidingScaleNote)] = "sliding_scale_note",
        [nameof(ListingForm.RegistrationMode)] = "registration",
        [nameof(ListingForm.ExternalUrl)] = "registration_url",
        [nameof(ListingForm.Capacity)] = "capacity",
        [nameof(ListingForm.TransportNote)] = "transport_note",
        [nameof(ListingForm.AccessibilityNote)] = "accessibility_note",
        [nameof(ListingForm.ContactEmail)] = "contact_email",
        [nameof(ListingForm.ContactPhone)] = "contact_phone",
        [nameof(ListingForm.Repeats)] = "repeats",
        [nameof(ListingForm.Days)] = "days",
        [nameof(ListingForm.Interval)] = "every_weeks",
        [nameof(ListingForm.StartsOn)] = "starts_on",
        [nameof(ListingForm.EndsOn)] = "ends_on",
        [nameof(ListingForm.StartTime)] = "start_time",
        [nameof(ListingForm.EndTime)] = "end_time",
        [nameof(ListingForm.SkipDates)] = "skip_dates",
        [nameof(ListingForm.DeadlineKind)] = "deadline_kind",
        [nameof(ListingForm.DeadlineOn)] = "deadline_on",
        [nameof(ListingForm.ShowFrom)] = "show_from",
        [nameof(ListingForm.TakeDownAfter)] = "take_down_after",
    };

    /// <summary>The template a provider downloads: the header and one example row.</summary>
    public static string Template()
    {
        string[] example =
        [
            "spring-robotics", "program", "Robotics Club", "Build and program robots with friends.", "", "en",
            "10", "14", "", "", "stem", "", "", "en;es", "in_person", "", "Main Street Center",
            "free", "", "", "", "external", "https://example.org/register", "", "no", "no", "", "no", "", "", "",
            "weekly", "TU;TH", "1", "2027-03-02", "2027-06-10", "15:30", "17:00", "2027-04-13;2027-04-15",
            "registration", "2027-02-20", "", "",
        ];
        return string.Join(',', Columns.Select(c => c.Name)) + "\r\n" + string.Join(',', example.Select(Quote)) + "\r\n";
    }

    /// <summary>
    /// Reads the records into import records, or explains why the file cannot be read: a
    /// missing or unknown column, or a row of the wrong width.
    /// </summary>
    public static (IReadOnlyList<ImportRecord>? Records, Phrase? Problem) Map(IReadOnlyList<CsvRecord> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count < 2)
        {
            return (null, Phrase.Of("import.error.no_rows"));
        }

        string[] header = [.. table[0].Fields.Select(h => h.Trim().ToLowerInvariant())];
        Dictionary<string, Column> known = Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        string[] unknown = [.. header.Where(h => !known.ContainsKey(h))];
        if (unknown.Length > 0)
        {
            return (null, Phrase.Of("import.error.unknown_columns", ("columns", string.Join(", ", unknown))));
        }

        string[] duplicate = [.. header.GroupBy(h => h, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)];
        if (duplicate.Length > 0)
        {
            return (null, Phrase.Of("import.error.duplicate_columns", ("columns", string.Join(", ", duplicate))));
        }

        string[] missing = [.. Required.Where(r => !header.Contains(r, StringComparer.Ordinal))];
        if (missing.Length > 0)
        {
            return (null, Phrase.Of("import.error.missing_columns", ("columns", string.Join(", ", missing))));
        }

        int idColumn = Array.IndexOf(header, "id");
        List<ImportRecord> records = [];
        foreach (CsvRecord row in table.Skip(1))
        {
            var record = new ImportRecord { Row = row.Line, ExternalId = row.Fields.Count > idColumn ? row.Fields[idColumn].Trim() : string.Empty };
            if (row.Fields.Count != header.Length)
            {
                record.Fail("*", Phrase.Of("import.error.wrong_width", ("expected", header.Length), ("actual", row.Fields.Count)));
            }
            else
            {
                for (int c = 0; c < header.Length; c++)
                {
                    known[header[c]].Apply(record, row.Fields[c].Trim());
                }
            }

            records.Add(record);
        }

        return (records, null);
    }

    private static string Or(string value, string fallback) => value.Length == 0 ? fallback : value.ToLowerInvariant();

    private static string[] List(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddAll(IList<string> target, IEnumerable<string> values)
    {
        foreach (string v in values)
        {
            target.Add(v);
        }
    }

    private static bool YesNo(ImportRecord record, string column, string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "yes" or "y" or "true" or "1":
                return true;
            case "" or "no" or "n" or "false" or "0":
                return false;
            default:
                record.Fail(column, Phrase.Of("import.error.yes_no"));
                return false;
        }
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
