namespace BrightTime.SharedKernel.Text;

/// <summary>The CLDR plural categories. Which ones a language uses varies: Arabic uses all six, Chinese one.</summary>
public enum PluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

/// <summary>
/// CLDR plural rules (cardinal) for the six launch locales.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than read from a library: there are six of them, they are stable, and
/// each is a line. From CLDR's <c>plurals.xml</c>, with its operands: <c>n</c> the absolute
/// value, <c>i</c> its integer digits, <c>v</c> the count of visible fraction digits.
/// </para>
/// <para>
/// Haitian Creole has no entry in CLDR, and the language does not inflect nouns for number
/// ("3 aktivite"), so it uses <see cref="PluralCategory.Other"/> throughout, as Chinese does.
/// </para>
/// </remarks>
public static class PluralRules
{
    private static readonly PluralCategory[] OneOther = [PluralCategory.One, PluralCategory.Other];
    private static readonly PluralCategory[] OtherOnly = [PluralCategory.Other];
    private static readonly PluralCategory[] AllSix =
        [PluralCategory.Zero, PluralCategory.One, PluralCategory.Two, PluralCategory.Few, PluralCategory.Many, PluralCategory.Other];

    public static PluralCategory Select(string locale, decimal number)
    {
        ArgumentNullException.ThrowIfNull(locale);

        decimal n = Math.Abs(number);
        decimal i = decimal.Truncate(n);
        int v = Scale(n);

        return Language(locale) switch
        {
            "en" => i == 1 && v == 0 ? PluralCategory.One : PluralCategory.Other,
            "es" => n == 1
                ? PluralCategory.One
                : i != 0 && i % 1_000_000 == 0 && v == 0 ? PluralCategory.Many : PluralCategory.Other,
            "bn" => i == 0 || n == 1 ? PluralCategory.One : PluralCategory.Other,
            "ar" => Arabic(n),
            _ => PluralCategory.Other,
        };
    }

    /// <summary>
    /// The categories a translation must give for the locale: those its whole numbers reach.
    /// Spanish <c>many</c> (exact millions) is optional and falls back to <c>other</c>.
    /// </summary>
    public static IReadOnlyList<PluralCategory> Required(string locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        return Language(locale) switch
        {
            "en" or "es" or "bn" => OneOther,
            "ar" => AllSix,
            _ => OtherOnly,
        };
    }

    public static string Name(PluralCategory category) => category.ToString().ToLowerInvariant();

    /// <summary>Reads a category as the catalogue writes it: <c>zero</c>, <c>one</c>, ... <c>other</c>.</summary>
    public static bool TryParse(string name, out PluralCategory category)
    {
        foreach (PluralCategory c in AllSix)
        {
            if (Name(c) == name)
            {
                category = c;
                return true;
            }
        }

        category = PluralCategory.Other;
        return false;
    }

    private static PluralCategory Arabic(decimal n)
    {
        if (n != decimal.Truncate(n))
        {
            return PluralCategory.Other;
        }

        decimal mod100 = n % 100;
        return n switch
        {
            0 => PluralCategory.Zero,
            1 => PluralCategory.One,
            2 => PluralCategory.Two,
            _ when mod100 is >= 3 and <= 10 => PluralCategory.Few,
            _ when mod100 is >= 11 and <= 99 => PluralCategory.Many,
            _ => PluralCategory.Other,
        };
    }

    private static string Language(string locale)
    {
        int dash = locale.IndexOf('-', StringComparison.Ordinal);
        return (dash < 0 ? locale : locale[..dash]).ToLowerInvariant();
    }

    private static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;
}
