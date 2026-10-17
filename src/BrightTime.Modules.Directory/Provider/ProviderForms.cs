using System.Globalization;
using BrightTime.Modules.Directory.Domain;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Directory.Provider;

/// <summary>An organisation's public details, as a form posts them: strings, checked by <see cref="ProviderForms"/>.</summary>
public sealed record OrganisationForm
{
    public string DisplayName { get; init; } = "";
    public string LegalName { get; init; } = "";
    public string Kind { get; init; } = "nonprofit";
    public string WebsiteUrl { get; init; } = "";
    public string Mission { get; init; } = "";
    public string PublicEmail { get; init; } = "";
    public string PublicPhone { get; init; } = "";
    public IReadOnlyList<string> Languages { get; init; } = [];
}

/// <summary>One opening period. All fields as posted.</summary>
public sealed record SiteHoursForm(string Day, string Opens, string Closes, string Season);

/// <summary>A site as a form posts it.</summary>
public sealed record SiteForm
{
    public string Name { get; init; } = "";
    public string AddressLine1 { get; init; } = "";
    public string AddressLine2 { get; init; } = "";
    public string Locality { get; init; } = "";
    public string PostalCode { get; init; } = "";
    public string NeighbourhoodId { get; init; } = "";
    public bool IsAddressPublic { get; init; } = true;
    public string EntranceNote { get; init; } = "";
    public string TransitNote { get; init; } = "";
    public string ParkingNote { get; init; } = "";

    /// <summary>"yes", "no", or "" for unknown: the schema keeps unknown apart from no.</summary>
    public string Wheelchair { get; init; } = "";
    public string Restroom { get; init; } = "";
    public string Elevator { get; init; } = "";
    public string QuietSpace { get; init; } = "";
    public IReadOnlyList<SiteHoursForm> Hours { get; init; } = [];

    /// <summary>Read only: whether the site is open for listings. Changed by its own action, not this form.</summary>
    public bool Active { get; init; } = true;
}

/// <summary>Checks provider forms. Every problem is a phrase the page renders in the reader's language.</summary>
public static class ProviderForms
{
    public const int MaxName = 200;
    public const int MaxMission = 2000;
    public const int MaxNote = 500;
    public const int MaxHours = 28;

    public static readonly string[] Kinds =
        ["nonprofit", "public_agency", "school", "library", "faith_based", "business", "higher_ed", "informal_group"];

    public static Dictionary<string, Phrase[]> Check(OrganisationForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Errors errors = new();
        errors.Required(nameof(form.DisplayName), form.DisplayName, MaxName);
        errors.Required(nameof(form.LegalName), form.LegalName, MaxName);
        if (!Kinds.Contains(form.Kind, StringComparer.Ordinal))
        {
            errors.Add(nameof(form.Kind), "provider.error.kind");
        }

        if (form.WebsiteUrl.Trim() is { Length: > 0 } url && !IsWebAddress(url))
        {
            errors.Add(nameof(form.WebsiteUrl), "provider.error.website");
        }

        errors.Optional(nameof(form.Mission), form.Mission, MaxMission);
        if (form.PublicEmail.Trim() is { Length: > 0 } email && !LooksLikeEmail(email))
        {
            errors.Add(nameof(form.PublicEmail), "editor.error.email");
        }

        errors.Optional(nameof(form.PublicPhone), form.PublicPhone, 40);
        if (form.Languages.Any(l => !Locale.TryFromCode(l, out _)))
        {
            errors.Add(nameof(form.Languages), "editor.error.languages");
        }

        return errors.Result;
    }

    public static Dictionary<string, Phrase[]> Check(SiteForm form, IReadOnlyCollection<Guid> neighbourhoods)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(neighbourhoods);
        Errors errors = new();
        errors.Required(nameof(form.Name), form.Name, MaxName);
        errors.Required(nameof(form.AddressLine1), form.AddressLine1, MaxName);
        errors.Optional(nameof(form.AddressLine2), form.AddressLine2, MaxName);
        errors.Required(nameof(form.Locality), form.Locality, 100);
        if (form.PostalCode.Trim() is { Length: > 0 } zip && !(zip.Length is 5 or 10 && zip[..5].All(char.IsAsciiDigit)))
        {
            errors.Add(nameof(form.PostalCode), "provider.error.postal_code");
        }

        // The neighbourhood places the site on the map and for distance search, and is all a
        // private address shows; so it is required.
        if (!Guid.TryParse(form.NeighbourhoodId, out Guid hood) || !neighbourhoods.Contains(hood))
        {
            errors.Add(nameof(form.NeighbourhoodId), "provider.error.neighbourhood");
        }

        foreach ((string field, string value) in new[]
                 {
                     (nameof(form.EntranceNote), form.EntranceNote), (nameof(form.TransitNote), form.TransitNote), (nameof(form.ParkingNote), form.ParkingNote),
                 })
        {
            errors.Optional(field, value, MaxNote);
        }

        foreach ((string field, string value) in new[]
                 {
                     (nameof(form.Wheelchair), form.Wheelchair), (nameof(form.Restroom), form.Restroom),
                     (nameof(form.Elevator), form.Elevator), (nameof(form.QuietSpace), form.QuietSpace),
                 })
        {
            if (value is not ("" or "yes" or "no"))
            {
                errors.Add(field, "editor.error.choose_option");
            }
        }

        List<SiteHoursForm> hours = [.. form.Hours.Where(h => !IsBlank(h))];
        if (hours.Count > MaxHours)
        {
            errors.Add("Hours", "provider.error.too_many_hours");
        }

        foreach (SiteHoursForm h in hours)
        {
            if (Hours(h) is null)
            {
                errors.Add("Hours", "provider.error.hours");
                break;
            }
        }

        return errors.Result;
    }

    public static bool IsBlank(SiteHoursForm h) =>
        h is null || (string.IsNullOrWhiteSpace(h.Opens) && string.IsNullOrWhiteSpace(h.Closes));

    /// <summary>A valid opening period, or null.</summary>
    public static SiteHours? Hours(SiteHoursForm h)
    {
        ArgumentNullException.ThrowIfNull(h);
        return short.TryParse(h.Day, NumberStyles.None, CultureInfo.InvariantCulture, out short day) && day is >= 0 and <= 6
               && TimeOnly.TryParseExact(h.Opens.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly opens)
               && TimeOnly.TryParseExact(h.Closes.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly closes)
               && closes > opens
               && SiteSeasons.All.Contains(h.Season, StringComparer.Ordinal)
            ? new SiteHours { DayOfWeek = day, OpensAt = opens, ClosesAt = closes, Season = h.Season }
            : null;
    }

    public static bool? TriState(string value) => value switch
    {
        "yes" => true,
        "no" => false,
        _ => null,
    };

    public static string TriState(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "",
    };

    public static bool IsWebAddress(string value) =>
        value is not null && Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    public static bool LooksLikeEmail(string value)
    {
        string v = value?.Trim() ?? string.Empty;
        int at = v.IndexOf('@', StringComparison.Ordinal);
        return v.Length <= 320 && at > 0 && at == v.LastIndexOf('@') && v.IndexOf('.', at) > at + 1 && !v.Contains(' ', StringComparison.Ordinal);
    }

    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Errors
    {
        private readonly Dictionary<string, List<Phrase>> _errors = new(StringComparer.Ordinal);

        public Dictionary<string, Phrase[]> Result => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);

        public void Add(string field, string key, params ReadOnlySpan<(string Name, object Value)> args)
        {
            if (!_errors.TryGetValue(field, out List<Phrase>? list))
            {
                _errors[field] = list = [];
            }

            list.Add(Phrase.Of(key, args));
        }

        public void Required(string field, string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Add(field, "provider.error.required");
            }
            else
            {
                Optional(field, value, max);
            }
        }

        public void Optional(string field, string value, int max)
        {
            if (value is { } v && v.Trim().Length > max)
            {
                Add(field, "editor.error.too_long", ("max", max), ("length", v.Trim().Length));
            }
        }
    }
}
