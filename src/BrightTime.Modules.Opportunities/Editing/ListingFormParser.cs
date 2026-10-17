using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Domain;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>A listing as the editor understood it: typed, and valid against the schema's rules.</summary>
public sealed record ParsedListing
{
    public required ListingKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public string? Description { get; init; }
    public required string PrimaryLanguage { get; init; }
    public short? AgeMin { get; init; }
    public short? AgeMax { get; init; }
    public short? GradeMin { get; init; }
    public short? GradeMax { get; init; }
    public required IReadOnlyList<Guid> TermIds { get; init; }
    public required IReadOnlyList<Guid> AccommodationIds { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required Modality Modality { get; init; }
    public string? VirtualUrl { get; init; }
    public required IReadOnlyList<Guid> SiteIds { get; init; }
    public required CostModel CostModel { get; init; }
    public int? CostCentsMin { get; init; }
    public int? CostCentsMax { get; init; }
    public string? SlidingScaleNote { get; init; }
    public required RegistrationMode RegistrationMode { get; init; }
    public string? ExternalUrl { get; init; }
    public int? CapacityTotal { get; init; }
    public bool MealsProvided { get; init; }
    public bool TransportProvided { get; init; }
    public string? TransportNote { get; init; }
    public bool IsDropIn { get; init; }
    public string? AccessibilityNote { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }
    public required DateOnly StartsOn { get; init; }
    public DateOnly? EndsOn { get; init; }
    public required TimeOnly StartTime { get; init; }
    public required TimeOnly EndTime { get; init; }
    public string? Rrule { get; init; }
    public string? DeadlineKind { get; init; }
    public DateOnly? DeadlineOn { get; init; }
}

/// <summary>
/// Turns what a provider typed into a <see cref="ParsedListing"/>, or into field-level
/// messages. Pure: the organisation's sites and the tenant's taxonomy are checked by the
/// editor, which has the database.
/// </summary>
public static class ListingFormParser
{
    public const int MaxTitle = 120;
    public const int MaxSummary = 300;
    public const int MaxDescription = 5_000;
    public const int MaxNote = 500;

    /// <summary>The longest a season may run, so a typo in the year cannot create ten years of sessions.</summary>
    public const int MaxSeasonDays = 400;

    public static readonly IReadOnlyList<string> DeadlineKinds =
        ["application", "registration", "audition", "early_bird", "scholarship", "document_submission"];

    public static readonly IReadOnlyList<string> DayCodes = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"];

    /// <summary>The editor's field order, for ordering error summaries.</summary>
    private static readonly string[] FieldOrder =
    [
        nameof(ListingForm.Kind), nameof(ListingForm.Title), nameof(ListingForm.Summary), nameof(ListingForm.Description),
        nameof(ListingForm.PrimaryLanguage), nameof(ListingForm.AgeMin), nameof(ListingForm.AgeMax),
        nameof(ListingForm.GradeMin), nameof(ListingForm.GradeMax), nameof(ListingForm.TermIds),
        nameof(ListingForm.Modality), nameof(ListingForm.SiteIds), nameof(ListingForm.VirtualUrl),
        nameof(ListingForm.Repeats), nameof(ListingForm.Days), nameof(ListingForm.StartsOn), nameof(ListingForm.EndsOn),
        nameof(ListingForm.StartTime), nameof(ListingForm.EndTime), nameof(ListingForm.CostModel),
        nameof(ListingForm.CostMin), nameof(ListingForm.CostMax), nameof(ListingForm.SlidingScaleNote),
        nameof(ListingForm.RegistrationMode), nameof(ListingForm.ExternalUrl), nameof(ListingForm.Capacity),
        nameof(ListingForm.DeadlineKind), nameof(ListingForm.DeadlineOn), nameof(ListingForm.AccommodationIds),
        nameof(ListingForm.AccessibilityNote), nameof(ListingForm.TransportNote), nameof(ListingForm.Languages),
        nameof(ListingForm.ContactEmail), nameof(ListingForm.ContactPhone),
    ];

    /// <summary>Validator keys that name a model field differently from the form.</summary>
    private static readonly Dictionary<string, string> FormKeys = new(StringComparer.Ordinal)
    {
        ["costCentsMin"] = nameof(ListingForm.CostMin),
        ["costCentsMax"] = nameof(ListingForm.CostMax),
        ["capacityTotal"] = nameof(ListingForm.Capacity),
        ["sites"] = nameof(ListingForm.SiteIds),
    };

    public static (ParsedListing? Listing, IReadOnlyDictionary<string, Phrase[]> Errors) Parse(ListingForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        Dictionary<string, List<Phrase>> errors = new(StringComparer.Ordinal);
        void Add(string field, Phrase message)
        {
            if (!errors.TryGetValue(field, out List<Phrase>? list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        ListingKind kind = Enum<ListingKind>(form.Kind, nameof(form.Kind), k => k.ToDbValue(), Add);
        Modality modality = Enum<Modality>(form.Modality, nameof(form.Modality), m => m.ToDbValue(), Add);
        CostModel cost = Enum<CostModel>(form.CostModel, nameof(form.CostModel), c => c.ToDbValue(), Add);
        RegistrationMode registration = Enum<RegistrationMode>(form.RegistrationMode, nameof(form.RegistrationMode), r => r.ToDbValue(), Add);

        string title = form.Title.Trim();
        string summary = form.Summary.Trim();
        Length(title, MaxTitle, nameof(form.Title), Add);
        Length(summary, MaxSummary, nameof(form.Summary), Add);
        Length(form.Description, MaxDescription, nameof(form.Description), Add);
        foreach ((string value, string field) in new[]
                 {
                     (form.SlidingScaleNote, nameof(form.SlidingScaleNote)),
                     (form.TransportNote, nameof(form.TransportNote)),
                     (form.AccessibilityNote, nameof(form.AccessibilityNote)),
                 })
        {
            Length(value, MaxNote, field, Add);
        }

        if (!Locale.TryFromCode(form.PrimaryLanguage, out _))
        {
            Add(nameof(form.PrimaryLanguage), Phrase.Of("editor.error.primary_language"));
        }

        string[] languages = [.. form.Languages.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal)];
        if (languages.Any(l => !Locale.TryFromCode(l, out _)))
        {
            Add(nameof(form.Languages), Phrase.Of("editor.error.languages"));
        }

        short? ageMin = Small(form.AgeMin, nameof(form.AgeMin), Add);
        short? ageMax = Small(form.AgeMax, nameof(form.AgeMax), Add);
        short? gradeMin = Small(form.GradeMin, nameof(form.GradeMin), Add);
        short? gradeMax = Small(form.GradeMax, nameof(form.GradeMax), Add);
        int? capacity = Whole(form.Capacity, nameof(form.Capacity), Add);

        // A free listing carries no price even if one was typed before switching to free.
        int? costMin = cost is CostModel.Fixed or CostModel.SlidingScale ? Cents(form.CostMin, nameof(form.CostMin), Add) : null;
        int? costMax = cost == CostModel.SlidingScale ? Cents(form.CostMax, nameof(form.CostMax), Add) : null;

        if (!string.IsNullOrWhiteSpace(form.ContactEmail) && !LooksLikeEmail(form.ContactEmail))
        {
            Add(nameof(form.ContactEmail), Phrase.Of("editor.error.email"));
        }

        // The schedule: one session, weekly on chosen days or a single date.
        DateOnly? startsOn = Date(form.StartsOn, nameof(form.StartsOn), Add, required: true);
        DateOnly? endsOn = Date(form.EndsOn, nameof(form.EndsOn), Add, required: false);
        TimeOnly? startTime = Time(form.StartTime, nameof(form.StartTime), Add);
        TimeOnly? endTime = Time(form.EndTime, nameof(form.EndTime), Add);
        if (startTime is { } st && endTime is { } et && et <= st)
        {
            Add(nameof(form.EndTime), Phrase.Of("editor.error.end_after_start"));
        }

        bool weekly = form.Repeats == "weekly";
        if (form.Repeats is not ("weekly" or "once"))
        {
            Add(nameof(form.Repeats), Phrase.Of("editor.error.repeats"));
        }

        string[] days = [.. DayCodes.Where(d => form.Days.Contains(d, StringComparer.Ordinal))];
        string? rrule = null;
        if (weekly)
        {
            if (days.Length == 0)
            {
                Add(nameof(form.Days), Phrase.Of("editor.error.days"));
            }

            if (endsOn is null && form.EndsOn.Length == 0 && kind != ListingKind.Resource)
            {
                Add(nameof(form.EndsOn), Phrase.Of("editor.error.ends_on_required"));
            }

            rrule = "FREQ=WEEKLY;BYDAY=" + string.Join(',', days);
        }
        else
        {
            endsOn = startsOn;
        }

        if (startsOn is { } s && endsOn is { } e)
        {
            if (e < s)
            {
                Add(nameof(form.EndsOn), Phrase.Of("editor.error.ends_before_starts"));
            }
            else if (e.DayNumber - s.DayNumber > MaxSeasonDays)
            {
                Add(nameof(form.EndsOn), Phrase.Of("editor.error.season_too_long", ("days", MaxSeasonDays)));
            }
        }

        // DTSTART must itself match the rule, or RFC 5545 makes it an extra occurrence.
        if (weekly && startsOn is { } first && days.Length > 0)
        {
            startsOn = days.Select(d => NextOrSame(first, d)).Min();
        }

        string? deadlineKind = string.IsNullOrWhiteSpace(form.DeadlineKind) ? null : form.DeadlineKind;
        DateOnly? deadlineOn = null;
        if (deadlineKind is not null)
        {
            if (!DeadlineKinds.Contains(deadlineKind, StringComparer.Ordinal))
            {
                Add(nameof(form.DeadlineKind), Phrase.Of("editor.error.deadline_kind"));
            }

            deadlineOn = Date(form.DeadlineOn, nameof(form.DeadlineOn), Add, required: true);
        }

        // The rules shared with the schema's CHECK constraints.
        Result rules = ListingValidator.Validate(new ListingDraft
        {
            Kind = kind,
            Title = title,
            Summary = summary,
            AgeMin = ageMin,
            AgeMax = ageMax,
            GradeMin = gradeMin,
            GradeMax = gradeMax,
            Modality = modality,
            VirtualUrl = Blank(form.VirtualUrl),
            CostModel = cost,
            CostCentsMin = costMin,
            CostCentsMax = costMax,
            RegistrationMode = registration,
            ExternalUrl = Blank(form.ExternalUrl),
            CapacityTotal = capacity,
            SiteCount = form.SiteIds.Count,
        });
        if (rules.Error?.Details is { } details)
        {
            foreach ((string key, Phrase[] messages) in details)
            {
                string field = FormKeys.TryGetValue(key, out string? mapped) ? mapped : char.ToUpperInvariant(key[0]) + key[1..];
                foreach (Phrase message in messages)
                {
                    // A field that failed to parse already says so; do not add a second message.
                    if (!errors.ContainsKey(field))
                    {
                        Add(field, message);
                    }
                }
            }
        }

        if (form.TermIds.Count == 0)
        {
            Add(nameof(form.TermIds), Phrase.Of("editor.error.categories"));
        }

        if (errors.Count > 0 || startsOn is null || startTime is null || endTime is null)
        {
            // In the order the fields appear, so the summary reads top to bottom like the form.
            return (null, errors
                .OrderBy(e => Array.IndexOf(FieldOrder, e.Key) is var i and >= 0 ? i : int.MaxValue)
                .ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        }

        return (new ParsedListing
        {
            Kind = kind,
            Title = title,
            Summary = summary,
            Description = Blank(form.Description),
            PrimaryLanguage = form.PrimaryLanguage,
            AgeMin = ageMin,
            AgeMax = ageMax,
            GradeMin = gradeMin,
            GradeMax = gradeMax,
            TermIds = [.. form.TermIds.Distinct()],
            AccommodationIds = [.. form.AccommodationIds.Distinct()],
            Languages = languages,
            Modality = modality,
            VirtualUrl = modality == Modality.InPerson ? null : Blank(form.VirtualUrl),
            SiteIds = modality == Modality.Virtual ? [] : [.. form.SiteIds.Distinct()],
            CostModel = cost,
            CostCentsMin = costMin,
            CostCentsMax = costMax,
            SlidingScaleNote = cost == CostModel.SlidingScale ? Blank(form.SlidingScaleNote) : null,
            RegistrationMode = registration,
            ExternalUrl = registration == RegistrationMode.External ? Blank(form.ExternalUrl) : null,
            CapacityTotal = capacity,
            MealsProvided = form.MealsProvided,
            TransportProvided = form.TransportProvided,
            TransportNote = Blank(form.TransportNote),
            IsDropIn = form.IsDropIn || kind == ListingKind.Resource,
            AccessibilityNote = Blank(form.AccessibilityNote),
            ContactEmail = Blank(form.ContactEmail),
            ContactPhone = Blank(form.ContactPhone),
            StartsOn = startsOn.Value,
            EndsOn = endsOn,
            StartTime = startTime.Value,
            EndTime = endTime.Value,
            Rrule = rrule,
            DeadlineKind = deadlineKind,
            DeadlineOn = deadlineOn,
        }, new Dictionary<string, Phrase[]>(StringComparer.Ordinal));
    }

    /// <summary>The next few sessions the schedule produces, for the editor's preview.</summary>
    public static IReadOnlyList<Occurrence> Preview(ParsedListing listing, string zone, DateOnly today, int count)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var rule = new SessionRule(zone, listing.StartsOn, listing.EndsOn, listing.StartTime, listing.EndTime, listing.Rrule, []);
        DateOnly from = listing.StartsOn > today ? listing.StartsOn : today;
        return [.. OccurrenceExpander.Expand(rule, from, from.AddDays(MaxSeasonDays)).Take(count)];
    }

    private static T Enum<T>(string value, string field, Func<T, string> toDb, Action<string, Phrase> add) where T : struct, System.Enum
    {
        foreach (T candidate in System.Enum.GetValues<T>())
        {
            if (string.Equals(toDb(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        add(field, Phrase.Of("editor.error.choose_option"));
        return default;
    }

    private static void Length(string? value, int max, string field, Action<string, Phrase> add)
    {
        if (value is { } v && v.Length > max)
        {
            add(field, Phrase.Of("editor.error.too_long", ("max", max), ("length", v.Length)));
        }
    }

    private static short? Small(string value, string field, Action<string, Phrase> add)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (short.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out short n))
        {
            return n;
        }

        add(field, Phrase.Of("editor.error.whole_number"));
        return null;
    }

    private static int? Whole(string value, string field, Action<string, Phrase> add)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            return n;
        }

        add(field, Phrase.Of("editor.error.whole_number"));
        return null;
    }

    /// <summary>Dollars to cents. "$1,250.50" is accepted; more than two decimal places is not.</summary>
    private static int? Cents(string value, string field, Action<string, Phrase> add)
    {
        string trimmed = value.Trim().TrimStart('$').Replace(",", string.Empty, StringComparison.Ordinal);
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal dollars)
            && decimal.Round(dollars, 2) == dollars && dollars <= 100_000m)
        {
            return (int)(dollars * 100m);
        }

        add(field, Phrase.Of("editor.error.dollars"));
        return null;
    }

    private static DateOnly? Date(string value, string field, Action<string, Phrase> add, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                add(field, Phrase.Of("editor.error.date_required"));
            }

            return null;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
        {
            return d;
        }

        add(field, Phrase.Of("editor.error.date_format"));
        return null;
    }

    private static TimeOnly? Time(string value, string field, Action<string, Phrase> add)
    {
        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t))
        {
            return t;
        }

        add(field, Phrase.Of(string.IsNullOrWhiteSpace(value) ? "editor.error.time_required" : "editor.error.time_format"));
        return null;
    }

    private static bool LooksLikeEmail(string value)
    {
        string v = value.Trim();
        int at = v.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == v.LastIndexOf('@') && v.IndexOf('.', at) > at + 1 && !v.Contains(' ', StringComparison.Ordinal);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly NextOrSame(DateOnly day, string code)
    {
        DayOfWeek target = code switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday,
        };

        while (day.DayOfWeek != target)
        {
            day = day.AddDays(1);
        }

        return day;
    }
}
