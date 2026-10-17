using System.Collections.Frozen;
using System.Globalization;

namespace BrightTime.SharedKernel.Primitives;

/// <summary>
/// The six launch locales, and the two facts about each that the rest of the
/// system needs: whether it is right-to-left, and which PostgreSQL text-search
/// configuration applies.
/// </summary>
/// <remarks>
/// Only English and Spanish have a Snowball stemmer in core PostgreSQL. The
/// other four fall back to <c>platform.ts_simple</c> and rely on the trigram
/// index for recall — see docs/plan/08-decisions.md D6.
/// </remarks>
public sealed record Locale
{
    public const string TextSearchEnglish = "platform.ts_en";
    public const string TextSearchSpanish = "platform.ts_es";
    public const string TextSearchSimple = "platform.ts_simple";

    private Locale(string code, string englishName, string nativeName, bool isRightToLeft, string textSearchConfig, DatePatterns dates)
    {
        Code = code;
        EnglishName = englishName;
        NativeName = nativeName;
        IsRightToLeft = isRightToLeft;
        TextSearchConfig = textSearchConfig;
        Dates = dates;
    }

    /// <summary>How this locale writes a date: word order and punctuation differ, not just names.</summary>
    public DatePatterns Dates { get; }

    public string Code { get; }

    public string EnglishName { get; }

    public string NativeName { get; }

    public bool IsRightToLeft { get; }

    /// <summary>The PostgreSQL <c>regconfig</c> name used to build and query the search vector.</summary>
    public string TextSearchConfig { get; }

    /// <summary>True when full-text stemming is unavailable and trigram matching is the primary path.</summary>
    public bool RequiresTrigramFallback => TextSearchConfig == TextSearchSimple;

    public string Direction => IsRightToLeft ? "rtl" : "ltr";

    public static readonly Locale English = new("en", "English", "English", false, TextSearchEnglish,
        new("dddd, MMMM d", "ddd, MMM d", "MMMM d, yyyy", "MMM d, yyyy"));

    public static readonly Locale Spanish = new("es", "Spanish", "Español", false, TextSearchSpanish,
        new("dddd, d 'de' MMMM", "ddd, d MMM", "d 'de' MMMM 'de' yyyy", "d MMM yyyy"));

    public static readonly Locale ChineseSimplified = new("zh-Hans", "Chinese (Simplified)", "简体中文", false, TextSearchSimple,
        new("M月d日dddd", "M月d日 ddd", "yyyy年M月d日", "yyyy年M月d日"));

    public static readonly Locale Bengali = new("bn", "Bengali", "বাংলা", false, TextSearchSimple,
        new("dddd, d MMMM", "ddd, d MMM", "d MMMM, yyyy", "d MMM, yyyy"));

    public static readonly Locale HaitianCreole = new("ht", "Haitian Creole", "Kreyòl ayisyen", false, TextSearchSimple,
        new("dddd d MMMM", "ddd d MMM", "d MMMM yyyy", "d MMM yyyy"));

    public static readonly Locale Arabic = new("ar", "Arabic", "العربية", true, TextSearchSimple,
        new("dddd، d MMMM", "ddd، d MMM", "d MMMM yyyy", "d MMM yyyy"));

    public static IReadOnlyList<Locale> Supported { get; } =
        [English, Spanish, ChineseSimplified, Bengali, HaitianCreole, Arabic];

    private static readonly FrozenDictionary<string, Locale> ByCode =
        Supported.ToFrozenDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    public static Locale Default => English;

    public static bool TryFromCode(string? code, out Locale locale)
    {
        if (!string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code, out Locale? found))
        {
            locale = found;
            return true;
        }

        locale = Default;
        return false;
    }

    /// <summary>
    /// Resolves a locale from an <c>Accept-Language</c>-style tag, falling back
    /// along the tag's parents: <c>zh-Hans-CN</c> → <c>zh-Hans</c> → <c>zh</c>.
    /// </summary>
    public static Locale Resolve(string? tag) => TryResolve(tag, out Locale found) ? found : Default;

    /// <summary>As <see cref="Resolve"/>, but says when nothing matched instead of answering English.</summary>
    public static bool TryResolve(string? tag, out Locale locale)
    {
        locale = Default;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string candidate = tag.Trim();

        // "zh", "zh-CN" and "zh-TW" all mean a Chinese reader, and Simplified is the one offered.
        if (candidate.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && (candidate.Length == 2 || candidate[2] == '-'))
        {
            locale = ChineseSimplified;
            return true;
        }

        while (candidate.Length > 0)
        {
            if (ByCode.TryGetValue(candidate, out Locale? found))
            {
                locale = found;
                return true;
            }

            int lastDash = candidate.LastIndexOf('-');
            if (lastDash <= 0)
            {
                break;
            }

            candidate = candidate[..lastDash];
        }

        return false;
    }

    /// <summary>
    /// The culture for names, numbers and times. Haitian Creole has no data in ICU, which would
    /// print "M10" for October and English day names, so it is built here.
    /// </summary>
    public CultureInfo ToCulture() => Code == HaitianCode ? Haitian.Value : CultureInfo.GetCultureInfo(Code);

    private const string HaitianCode = "ht";

    private static readonly Lazy<CultureInfo> Haitian = new(BuildHaitian);

    /// <summary>
    /// Kreyòl month and day names on French conventions, which Haiti follows: a 24-hour clock,
    /// a decimal comma, day before month.
    /// </summary>
    private static CultureInfo BuildHaitian()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo(HaitianCode).Clone();
        CultureInfo french = CultureInfo.GetCultureInfo("fr");

        DateTimeFormatInfo dates = (DateTimeFormatInfo)french.DateTimeFormat.Clone();
        dates.DayNames = ["dimanch", "lendi", "madi", "mèkredi", "jedi", "vandredi", "samdi"];
        dates.AbbreviatedDayNames = ["dim", "len", "mad", "mèk", "jed", "van", "sam"];
        dates.ShortestDayNames = ["di", "le", "ma", "mè", "je", "va", "sa"];
        string[] months = ["janvye", "fevriye", "mas", "avril", "me", "jen", "jiyè", "out", "septanm", "oktòb", "novanm", "desanm", ""];
        string[] shortMonths = ["jan", "fev", "mas", "avr", "me", "jen", "jiy", "out", "sep", "okt", "nov", "des", ""];
        dates.MonthNames = months;
        dates.MonthGenitiveNames = months;
        dates.AbbreviatedMonthNames = shortMonths;
        dates.AbbreviatedMonthGenitiveNames = shortMonths;
        dates.LongDatePattern = "dddd d MMMM yyyy";
        dates.MonthDayPattern = "d MMMM";
        dates.ShortTimePattern = "HH:mm";
        dates.LongTimePattern = "HH:mm:ss";
        culture.DateTimeFormat = dates;
        culture.NumberFormat = (NumberFormatInfo)french.NumberFormat.Clone();

        return CultureInfo.ReadOnly(culture);
    }

    public override string ToString() => Code;
}

/// <summary>
/// A locale's date patterns, as .NET custom format strings. ICU's skeleton-based patterns are not
/// exposed by .NET, and a pattern written once in English order ("MMM d, h:mm tt") is wrong in
/// every other language, so each locale states its own.
/// </summary>
/// <param name="DayLong">A weekday and date, no year: "Saturday, October 17".</param>
/// <param name="DayShort">The same, abbreviated: "Sat, Oct 17".</param>
/// <param name="Date">A date with its year: "October 17, 2026".</param>
/// <param name="DateShort">The same, abbreviated: "Oct 17, 2026".</param>
public sealed record DatePatterns(string DayLong, string DayShort, string Date, string DateShort);
