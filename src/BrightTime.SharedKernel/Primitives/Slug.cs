using System.Globalization;
using System.Text;

namespace BrightTime.SharedKernel.Primitives;

/// <summary>
/// URL slug generation for listings, organisations, collections and playlists.
/// </summary>
/// <remarks>
/// <para>
/// Four of our six locales are non-Latin, so transliterating a title is not
/// generally possible. The rule is to keep whatever Unicode letters and digits
/// the title contains and let the URL be percent-encoded, rather than emit an
/// empty or meaningless slug.
/// </para>
/// <para>
/// Combining marks are handled by script, not uniformly. For Latin text a mark
/// is a diacritic and folding it is the point ("Español" → "espanol"). For
/// Bengali, Arabic and other scripts a mark is part of the orthography — the
/// Bengali virama (U+09CD) forms conjuncts and the vowel signs are letters in
/// every sense that matters — so stripping them produces a misspelling, not a
/// simplification. A mark is therefore dropped only when it follows an ASCII
/// letter.
/// </para>
/// </remarks>
public static class Slug
{
    public const int MaxLength = 80;

    public static string From(string? text, string? fallback = null)
    {
        string normalised = Normalise(text);
        if (normalised.Length > 0)
        {
            return normalised;
        }

        string normalisedFallback = Normalise(fallback);
        return normalisedFallback.Length > 0 ? normalisedFallback : "item";
    }

    private static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool lastWasSeparator = false;
        char lastAppended = '\0';

        foreach (char ch in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);

            if (category is UnicodeCategory.NonSpacingMark
                         or UnicodeCategory.SpacingCombiningMark
                         or UnicodeCategory.EnclosingMark)
            {
                // A diacritic on a Latin letter is decoration: fold it away.
                // A mark on anything else is orthography: keep it.
                if (builder.Length == 0 || lastWasSeparator || char.IsAscii(lastAppended))
                {
                    continue;
                }

                builder.Append(ch);
                lastAppended = ch;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                char lower = char.ToLowerInvariant(ch);
                builder.Append(lower);
                lastAppended = lower;
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastAppended = '-';
                lastWasSeparator = true;
            }
        }

        string result = builder.ToString().Trim('-').Normalize(NormalizationForm.FormC);
        return result.Length > MaxLength ? TruncateOnGraphemeBoundary(result, MaxLength) : result;
    }

    /// <summary>
    /// Truncates without splitting a grapheme cluster. Cutting mid-cluster in
    /// Bengali or Arabic leaves an orphaned combining mark, which renders as a
    /// dotted circle.
    /// </summary>
    private static string TruncateOnGraphemeBoundary(string value, int maxLength)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        int lastSafeEnd = 0;

        while (enumerator.MoveNext())
        {
            string element = (string)enumerator.Current;
            int end = enumerator.ElementIndex + element.Length;
            if (end > maxLength)
            {
                break;
            }

            lastSafeEnd = end;
        }

        return value[..lastSafeEnd].TrimEnd('-');
    }
}
