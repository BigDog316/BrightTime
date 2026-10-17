using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Web.Localisation;

/// <summary>
/// The interface's words for this request: in its locale, with the tenant's overrides, and
/// with dates, times and money written the way the locale writes them.
/// </summary>
/// <remarks>
/// <para>
/// Pages inject it as <c>T</c>: <c>@T["finder.title"]</c>,
/// <c>@T.Format("finder.results.count", ("count", n))</c>, <c>@T[phrase]</c> for what a module
/// returned.
/// </para>
/// <para>
/// A string is looked up in the tenant's override for the locale, then the catalogue for the
/// locale, then English (override, then catalogue). <see cref="LoadAsync"/> must run first,
/// which <see cref="LocaleMiddleware"/> does for every request, because Razor reads strings
/// synchronously.
/// </para>
/// </remarks>
public sealed partial class Text(Catalogue catalogue, ITenantContext tenant, IWordingOverrides overrides, ILogger<Text> logger)
{
    private static readonly IReadOnlyDictionary<string, object> NoArguments = new Dictionary<string, object>();

    private WordingSet _overrides = WordingSet.Empty;

    public Locale Locale => tenant.Locale;

    public CultureInfo Culture => tenant.Locale.ToCulture();

    /// <summary>Loads the tenant's overrides. Safe to call more than once.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        if (tenant.IsResolved)
        {
            _overrides = await overrides.ForTenantAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        }
    }

    public string this[string key] => Render(key, NoArguments);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1043:Use integral or string argument for indexers",
        Justification = "T[phrase] reads like T[key] in markup, which is the point: a phrase is a key with arguments.")]
    public string this[Phrase phrase]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(phrase);
            return Render(phrase.Key, phrase.Arguments);
        }
    }

    public string Format(string key, params ReadOnlySpan<(string Name, object Value)> arguments)
    {
        Dictionary<string, object> values = new(arguments.Length, StringComparer.Ordinal);
        foreach ((string name, object value) in arguments)
        {
            values[name] = value;
        }

        return Render(key, values);
    }

    /// <summary>A module's error, in this locale. Falls back to the module's own English if the catalogue has no entry.</summary>
    public string Error(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (catalogue.Has(error.Phrase.Key))
        {
            return this[error.Phrase];
        }

        LogMissing(logger, error.Phrase.Key);
        return error.Message;
    }

    /// <summary>Several phrases as one sentence run, for a field with more than one problem.</summary>
    public string Join(IEnumerable<Phrase> phrases) => string.Join(" ", phrases.Select(p => this[p]));

    public bool Has(string key) => catalogue.Has(key);

    /// <summary>
    /// A name to put inside a sentence (an organisation, a provider), wrapped in Unicode first-
    /// strong isolates so its direction cannot reorder the sentence around it: an English name
    /// in an Arabic sentence, or the reverse. The markup equivalent is <c>&lt;bdi&gt;</c>, which a
    /// catalogue string cannot hold.
    /// </summary>
    public static string Name(string name) => "\u2068" + name + "\u2069";

    // ---- dates, numbers, money -------------------------------------------------------------

    /// <summary>"Saturday, October 17", in the tenant's time zone.</summary>
    public string DayLong(DateTimeOffset at) => Local(at).ToString(Locale.Dates.DayLong, Culture);

    /// <summary>"Sat, Oct 17".</summary>
    public string DayShort(DateTimeOffset at) => Local(at).ToString(Locale.Dates.DayShort, Culture);

    /// <summary>"October 17, 2026".</summary>
    public string Date(DateTimeOffset at) => Local(at).ToString(Locale.Dates.Date, Culture);

    /// <summary>"Oct 17, 2026".</summary>
    public string DateShort(DateTimeOffset at) => Local(at).ToString(Locale.Dates.DateShort, Culture);

    /// <summary>The locale's short time: "3:30 PM", "15:30".</summary>
    public string Time(DateTimeOffset at) => Local(at).ToString("t", Culture);

    /// <summary>A date and a time, joined as the locale joins them.</summary>
    public string DateTime(DateTimeOffset at) => Format("format.date_time", ("date", DateShort(at)), ("time", Time(at)));

    /// <summary>A weekday, date and time: "Sat, Oct 17, 3:30 PM".</summary>
    public string DayTime(DateTimeOffset at) => Format("format.date_time", ("date", DayShort(at)), ("time", Time(at)));

    /// <summary>"3:30 PM – 5:00 PM", or the locale's form of it.</summary>
    public string Range(string from, string to) => Format("format.range", ("from", from), ("to", to));

    public string Number(decimal value) => value.ToString("#,0.##", Culture);

    public string Money(int cents) => new Money(cents).Format(Locale);

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, tenant.TimeZone);

    // ---- rendering --------------------------------------------------------------------------

    private string Render(string key, IReadOnlyDictionary<string, object> arguments)
    {
        if (!TryTemplate(key, out MessageTemplate template, out string locale))
        {
            LogMissing(logger, key);
            return "[" + key + "]";
        }

        return template.Format(locale, arguments, RenderValue);
    }

    private bool TryTemplate(string key, out MessageTemplate template, out string locale)
    {
        string code = Locale.Code;
        if (_overrides.TryGet(code, key, out template) || catalogue.TryGet(code, key, out template, out _))
        {
            locale = code;
            return true;
        }

        // A gap the tests should have caught: English rather than nothing, with English plurals.
        locale = SharedKernel.Primitives.Locale.English.Code;
        return _overrides.TryGet(locale, key, out template) || catalogue.TryGet(locale, key, out template, out _);
    }

    private string RenderValue(object value) => value switch
    {
        string s => s,
        Phrase p => this[p],
        int or long or short or decimal or double or float => Number(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        Money m => m.Format(Locale),
        DateTimeOffset d => DateShort(d),
        _ => Convert.ToString(value, Culture) ?? string.Empty,
    };

    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "No catalogue entry for {Key}.")]
    private static partial void LogMissing(ILogger logger, string key);
}
