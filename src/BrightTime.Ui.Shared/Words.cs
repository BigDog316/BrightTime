using System.Globalization;
using System.Text.Json;
using BrightTime.Localisation;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Ui.Shared.Api;

namespace BrightTime.Ui.Shared;

/// <summary>The app's sentences that are built from API values, in one place.</summary>
public static class Words
{
    /// <summary>A sentence the API sent as a key and arguments (D30's "why", notices).</summary>
    public static string Phrase(Text t, Phrase phrase)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(phrase);
        if (!t.Has(phrase.Key))
        {
            // A key this build does not know yet (the API is newer): say nothing rather than a code.
            return string.Empty;
        }

        (string Name, object Value)[] arguments = [.. (phrase.Args ?? new Dictionary<string, JsonElement>()).Select(a => (a.Key, Value(a.Value)))];
        return t.Format(phrase.Key, arguments);

        static object Value(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// What went wrong, in words: the error's own sentence when the catalogue has one, else a
    /// general one. Never the server's English title.
    /// </summary>
    public static string Problem(Text t, ApiProblem problem)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(problem);
        return problem switch
        {
            { Type: ApiProblem.OfflineType } => t["app.error.offline"],
            { Status: 401 } => t["app.error.signed_out"],
            { Status: 429 } => t["app.error.too_many"],
            { Code: { } code } when t.Has("error." + code) => t["error." + code],
            _ => t["site.error.body"],
        };
    }

    /// <summary>The price line of a result.</summary>
    public static string Price(Text t, Cost cost)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(cost);
        return cost switch
        {
            { IsFree: true } => t["listing.price.free"],
            { FromCents: { } cents } => t.Format("finder.results.price_from", ("price", t.Money(cents))),
            _ => t["finder.results.price_varies"],
        };
    }

    /// <summary>The ages a listing is for, or null when it does not say.</summary>
    public static string? Ages(Text t, int? min, int? max)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (min, max) switch
        {
            ({ } a, { } b) => t.Format("listing.ages.range", ("min", a), ("max", b)),
            ({ } a, null) => t.Format("listing.ages.from", ("min", a)),
            (null, { } b) => t.Format("listing.ages.up_to", ("max", b)),
            _ => null,
        };
    }

    /// <summary>The <c>yyyy-MM</c> the API takes for a birth month.</summary>
    public static string BirthMonth(int year, int month) =>
        new DateOnly(year, month, 1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a birth month is under 13 on <paramref name="today"/>, by the server's younger
    /// reading. For the screens only; the server decides.
    /// </summary>
    public static bool IsUnder13(int year, int month, DateOnly today) =>
        Age.BandFromBirthMonth(new DateOnly(year, month, 1), today).IsCoppaProtected();

    /// <summary>A month's name in the app's language, for the age question.</summary>
    public static string MonthName(Text t, int month)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Culture.DateTimeFormat.GetMonthName(month);
    }
}
