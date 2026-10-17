using System.Globalization;
using System.Text.Json;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Import;

/// <summary>
/// Reads Open Referral HSDS 3.0 services: an array of services, an object with a
/// <c>services</c> array, or a page of the HSDS API (<c>contents</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each service becomes a program. HSDS has no summary, so the first sentence of the
/// description stands in, shortened to fit. Sites are matched by the name of each
/// <c>service_at_locations[].location</c>; terms by <c>attributes[].taxonomy_term.code</c>.
/// Codes this platform does not know are ignored rather than refused, because HSDS
/// publishers use many taxonomies; the upload's default category covers a service left
/// with none.
/// </para>
/// <para>
/// The schedule is the first weekly one with times. Monthly and other patterns are refused
/// for that row, not guessed at. Organisation ids in the file are ignored: everything imports
/// into the uploading organisation.
/// </para>
/// </remarks>
public static class HsdsListingFormat
{
    public static (IReadOnlyList<ImportRecord>? Records, Phrase? Problem) Map(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return (null, Phrase.Of("import.error.bad_json"));
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            JsonElement services = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("services", out JsonElement s) && s.ValueKind == JsonValueKind.Array ? s
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contents", out JsonElement c) && c.ValueKind == JsonValueKind.Array ? c
                : default;
            if (services.ValueKind != JsonValueKind.Array)
            {
                return (null, Phrase.Of("import.error.not_hsds"));
            }

            List<ImportRecord> records = [];
            int row = 0;
            foreach (JsonElement service in services.EnumerateArray())
            {
                row++;
                records.Add(service.ValueKind == JsonValueKind.Object ? Read(service, row) : Broken(row));
            }

            return records.Count == 0 ? (null, Phrase.Of("import.error.no_rows")) : (records, null);
        }
    }

    private static ImportRecord Broken(int row)
    {
        var record = new ImportRecord { Row = row, ExternalId = string.Empty };
        record.Fail("*", Phrase.Of("import.error.not_hsds"));
        return record;
    }

    private static ImportRecord Read(JsonElement service, int row)
    {
        var record = new ImportRecord { Row = row, ExternalId = Text(service, "id") };
        ListingForm form = record.Form;

        if (Text(service, "status") is { Length: > 0 } status && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            record.Fail("status", Phrase.Of("import.error.hsds_inactive", ("status", status)));
        }

        form.Kind = "program";
        form.Title = Text(service, "name");
        string description = Text(service, "description");
        form.Description = description;
        form.Summary = SummaryOf(description, ListingFormParser.MaxSummary);
        form.AgeMin = Age(service, "minimum_age", Math.Ceiling);
        form.AgeMax = Age(service, "maximum_age", Math.Floor);
        form.ContactEmail = Text(service, "email");
        form.ContactPhone = Each(service, "phones").Select(p => Text(p, "number")).FirstOrDefault(n => n.Length > 0) ?? string.Empty;

        string url = Text(service, "url");
        form.RegistrationMode = url.Length > 0 ? "external" : "none";
        form.ExternalUrl = url;

        foreach (string code in Each(service, "languages").Select(l => Text(l, "code")).Where(c => c.Length > 0))
        {
            form.Languages.Add(code.ToLowerInvariant());
        }

        foreach (string site in Each(service, "service_at_locations")
                     .Select(sal => sal.TryGetProperty("location", out JsonElement location) && location.ValueKind == JsonValueKind.Object ? Text(location, "name") : string.Empty)
                     .Where(n => n.Length > 0))
        {
            record.SiteNames.Add(site);
        }
        // No location: a service delivered online, reached at its own address.
        form.Modality = record.SiteNames.Count > 0 ? "in_person" : "virtual";
        form.VirtualUrl = record.SiteNames.Count > 0 ? string.Empty : url;

        // Unknown codes are ignored (see remarks), so they go to the category list only to be looked up.
        foreach (string code in Each(service, "attributes")
                     .Select(a => a.TryGetProperty("taxonomy_term", out JsonElement term) && term.ValueKind == JsonValueKind.Object ? Text(term, "code") : string.Empty)
                     .Where(c => c.Length > 0))
        {
            record.CategoryCodes.Add(code);
        }

        Cost(service, form);
        Schedule(service, record);
        return record;
    }

    private static void Cost(JsonElement service, ListingForm form)
    {
        decimal[] amounts = [.. Each(service, "cost_options")
            .Select(o => o.TryGetProperty("amount", out JsonElement a) && a.TryGetDecimal(out decimal d) ? d : -1)
            .Where(d => d >= 0)];
        if (amounts.Length == 0)
        {
            form.CostModel = Text(service, "fees_description").Length > 0 ? "varies" : "free";
        }
        else if (amounts.All(a => a == 0))
        {
            form.CostModel = "free";
        }
        else if (amounts.Length == 1)
        {
            form.CostModel = "fixed";
            form.CostMin = amounts[0].ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            form.CostModel = "sliding_scale";
            form.CostMin = amounts.Min().ToString(CultureInfo.InvariantCulture);
            form.CostMax = amounts.Max().ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void Schedule(JsonElement service, ImportRecord record)
    {
        JsonElement[] schedules = [.. Each(service, "schedules")];
        JsonElement? weekly = schedules.Cast<JsonElement?>().FirstOrDefault(s =>
            string.Equals(Text(s!.Value, "freq"), "WEEKLY", StringComparison.OrdinalIgnoreCase) && Text(s.Value, "opens_at").Length > 0);
        JsonElement? once = schedules.Cast<JsonElement?>().FirstOrDefault(s =>
            Text(s!.Value, "freq").Length == 0 && Text(s.Value, "opens_at").Length > 0);

        ListingForm form = record.Form;
        if (weekly is { } w)
        {
            string[] days = [.. Text(w, "byday").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.ToUpperInvariant())];
            if (days.Length == 0 || days.Any(d => !ListingFormParser.DayCodes.Contains(d, StringComparer.Ordinal)))
            {
                record.Fail("schedules", Phrase.Of("import.error.hsds_schedule"));
                return;
            }

            form.Repeats = "weekly";
            foreach (string d in days)
            {
                form.Days.Add(d);
            }

            form.Interval = Text(w, "interval") is { Length: > 0 } interval ? interval : "1";
            form.StartsOn = Day(w, "dtstart") is { Length: > 0 } start ? start : Day(w, "valid_from");
            form.EndsOn = Day(w, "until") is { Length: > 0 } until ? until : Day(w, "valid_to");
            form.StartTime = Clock(Text(w, "opens_at"));
            form.EndTime = Clock(Text(w, "closes_at"));
        }
        else if (once is { } o)
        {
            form.Repeats = "once";
            form.StartsOn = Day(o, "dtstart") is { Length: > 0 } start ? start : Day(o, "valid_from");
            form.StartTime = Clock(Text(o, "opens_at"));
            form.EndTime = Clock(Text(o, "closes_at"));
        }
        else
        {
            record.Fail("schedules", Phrase.Of("import.error.hsds_schedule"));
        }
    }

    /// <summary>The first sentence, or as many whole words as fit.</summary>
    internal static string SummaryOf(string description, int max)
    {
        string text = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        int end = text.IndexOfAny(['.', '!', '?']);
        string sentence = end > 0 ? text[..(end + 1)] : text;
        if (sentence.Length <= max)
        {
            return sentence;
        }

        int cut = sentence.LastIndexOf(' ', max - 1);
        return (cut > 0 ? sentence[..cut] : sentence[..(max - 1)]).TrimEnd(',', ';', ':') + "…";
    }

    private static IEnumerable<JsonElement> Each(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    /// <summary>yyyy-MM-dd from an HSDS date or date-time.</summary>
    private static string Day(JsonElement element, string name)
    {
        string value = Text(element, name);
        return value.Length >= 10 ? value[..10] : value;
    }

    /// <summary>HH:mm from HSDS's HH:mm:ss, with any zone suffix dropped: the tenant's zone applies.</summary>
    private static string Clock(string value) => value.Length >= 5 ? value[..5] : value;

    private static string Age(JsonElement element, string name, Func<double, double> round) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double age)
            ? round(age).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
}
