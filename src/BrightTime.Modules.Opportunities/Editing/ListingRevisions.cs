using System.Globalization;
using System.Text.Json;

namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>One field that differs between two versions of a listing. A list field holds its values sorted.</summary>
public sealed record FieldChange(string Field, IReadOnlyList<string> Before, IReadOnlyList<string> After)
{
    /// <summary>A field the form holds as several values (days, sites, terms, languages).</summary>
    public bool IsList { get; init; }
}

/// <summary>What changed since an organiser last reviewed a listing, and when that was.</summary>
public sealed record ReviewDiff(DateTimeOffset ReviewedAt, IReadOnlyList<FieldChange> Changes);

/// <summary>
/// Listing revisions (D15): the editor's form as it stood at a submission or a review, stored
/// as JSON, and compared field by field.
/// </summary>
/// <remarks>
/// The snapshot is the form rather than the rows behind it. That is what a provider typed and
/// an organiser approved, it reads the same in a diff as in the editor, and it does not change
/// shape when a table does. Ids (sites, terms) are stored as ids and named when shown, so a
/// term renamed since still reads under its current name.
/// </remarks>
public static class ListingRevisions
{
    public const string Submitted = "submitted";

    public const string Reviewed = "reviewed";

    /// <summary>The snapshot's shape. Bumped if the form changes in a way old snapshots cannot be read as.</summary>
    private const int Format = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Snapshot(int Format, ListingForm Form);

    private static readonly (string Field, bool IsList, Func<ListingForm, IEnumerable<string>> Values)[] Fields =
    [
        (nameof(ListingForm.Kind), false, f => [f.Kind]),
        (nameof(ListingForm.Title), false, f => [f.Title]),
        (nameof(ListingForm.Summary), false, f => [f.Summary]),
        (nameof(ListingForm.Description), false, f => [f.Description]),
        (nameof(ListingForm.PrimaryLanguage), false, f => [f.PrimaryLanguage]),
        (nameof(ListingForm.AgeMin), false, f => [f.AgeMin]),
        (nameof(ListingForm.AgeMax), false, f => [f.AgeMax]),
        (nameof(ListingForm.GradeMin), false, f => [f.GradeMin]),
        (nameof(ListingForm.GradeMax), false, f => [f.GradeMax]),
        (nameof(ListingForm.TermIds), true, f => f.TermIds.Select(Id)),
        (nameof(ListingForm.Modality), false, f => [f.Modality]),
        (nameof(ListingForm.SiteIds), true, f => f.SiteIds.Select(Id)),
        (nameof(ListingForm.VirtualUrl), false, f => [f.VirtualUrl]),
        (nameof(ListingForm.Repeats), false, f => [f.Repeats]),
        (nameof(ListingForm.Days), true, f => f.Days),
        (nameof(ListingForm.Interval), false, f => [f.Interval]),
        (nameof(ListingForm.StartsOn), false, f => [f.StartsOn]),
        (nameof(ListingForm.EndsOn), false, f => [f.EndsOn]),
        (nameof(ListingForm.StartTime), false, f => [f.StartTime]),
        (nameof(ListingForm.EndTime), false, f => [f.EndTime]),
        (nameof(ListingForm.SkipDates), true, f => f.SkipDates.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        (nameof(ListingForm.ShowFrom), false, f => [f.ShowFrom]),
        (nameof(ListingForm.TakeDownAfter), false, f => [f.TakeDownAfter]),
        (nameof(ListingForm.CostModel), false, f => [f.CostModel]),
        (nameof(ListingForm.CostMin), false, f => [f.CostMin]),
        (nameof(ListingForm.CostMax), false, f => [f.CostMax]),
        (nameof(ListingForm.SlidingScaleNote), false, f => [f.SlidingScaleNote]),
        (nameof(ListingForm.RegistrationMode), false, f => [f.RegistrationMode]),
        (nameof(ListingForm.ExternalUrl), false, f => [f.ExternalUrl]),
        (nameof(ListingForm.Capacity), false, f => [f.Capacity]),
        (nameof(ListingForm.DeadlineKind), false, f => [f.DeadlineKind]),
        (nameof(ListingForm.DeadlineOn), false, f => [f.DeadlineOn]),
        (nameof(ListingForm.AccommodationIds), true, f => f.AccommodationIds.Select(Id)),
        (nameof(ListingForm.AccessibilityNote), false, f => [f.AccessibilityNote]),
        (nameof(ListingForm.MealsProvided), false, f => [Flag(f.MealsProvided)]),
        (nameof(ListingForm.TransportProvided), false, f => [Flag(f.TransportProvided)]),
        (nameof(ListingForm.TransportNote), false, f => [f.TransportNote]),
        (nameof(ListingForm.IsDropIn), false, f => [Flag(f.IsDropIn)]),
        (nameof(ListingForm.Languages), true, f => f.Languages),
        (nameof(ListingForm.ContactEmail), false, f => [f.ContactEmail]),
        (nameof(ListingForm.ContactPhone), false, f => [f.ContactPhone]),
    ];

    /// <summary>The form as stored. The row version is left out: it is not content.</summary>
    public static string Serialise(ListingForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        string version = form.Version;
        form.Version = string.Empty;
        try
        {
            return JsonSerializer.Serialize(new Snapshot(Format, form), Json);
        }
        finally
        {
            form.Version = version;
        }
    }

    /// <summary>A stored form, or null when it cannot be read as this version's form.</summary>
    public static ListingForm? Deserialise(string json)
    {
        try
        {
            Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(json, Json);
            return snapshot is { Format: Format } ? snapshot.Form : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The fields that differ, in the editor's order. Text is compared trimmed, with line endings made alike; lists as sets.</summary>
    public static IReadOnlyList<FieldChange> Compare(ListingForm before, ListingForm after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<FieldChange> changes = [];
        foreach ((string field, bool isList, Func<ListingForm, IEnumerable<string>> values) in Fields)
        {
            string[] a = Normalise(values(before), isList);
            string[] b = Normalise(values(after), isList);
            if (!a.SequenceEqual(b, StringComparer.Ordinal))
            {
                changes.Add(new FieldChange(field, a, b) { IsList = isList });
            }
        }

        return changes;
    }

    private static string[] Normalise(IEnumerable<string> values, bool isList)
    {
        IEnumerable<string> cleaned = values
            .Select(v => (v ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        return isList
            ? [.. cleaned.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            : [.. cleaned];
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "true" : "false";
}
