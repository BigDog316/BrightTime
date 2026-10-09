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

    private Locale(string code, string englishName, string nativeName, bool isRightToLeft, string textSearchConfig)
    {
        Code = code;
        EnglishName = englishName;
        NativeName = nativeName;
        IsRightToLeft = isRightToLeft;
        TextSearchConfig = textSearchConfig;
    }

    public string Code { get; }

    public string EnglishName { get; }

    public string NativeName { get; }

    public bool IsRightToLeft { get; }

    /// <summary>The PostgreSQL <c>regconfig</c> name used to build and query the search vector.</summary>
    public string TextSearchConfig { get; }

    /// <summary>True when full-text stemming is unavailable and trigram matching is the primary path.</summary>
    public bool RequiresTrigramFallback => TextSearchConfig == TextSearchSimple;

    public string Direction => IsRightToLeft ? "rtl" : "ltr";

    public static readonly Locale English = new("en", "English", "English", false, TextSearchEnglish);
    public static readonly Locale Spanish = new("es", "Spanish", "Español", false, TextSearchSpanish);
    public static readonly Locale ChineseSimplified = new("zh-Hans", "Chinese (Simplified)", "简体中文", false, TextSearchSimple);
    public static readonly Locale Bengali = new("bn", "Bengali", "বাংলা", false, TextSearchSimple);
    public static readonly Locale HaitianCreole = new("ht", "Haitian Creole", "Kreyòl ayisyen", false, TextSearchSimple);
    public static readonly Locale Arabic = new("ar", "Arabic", "العربية", true, TextSearchSimple);

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
    public static Locale Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Default;
        }

        string candidate = tag.Trim();
        while (candidate.Length > 0)
        {
            if (ByCode.TryGetValue(candidate, out Locale? found))
            {
                return found;
            }

            int lastDash = candidate.LastIndexOf('-');
            if (lastDash <= 0)
            {
                break;
            }

            candidate = candidate[..lastDash];
        }

        return Default;
    }

    public CultureInfo ToCulture() => CultureInfo.GetCultureInfo(Code);

    public override string ToString() => Code;
}
