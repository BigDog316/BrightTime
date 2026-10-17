using System.Text.RegularExpressions;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Localisation;

namespace BrightTime.Web.Tests.Localisation;

/// <summary>
/// The plan's reading-level target (docs/plan/06-screens.md): Flesch-Kincaid grade 8 or below on
/// every youth-facing string, checked against the English catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Youth-facing means the public site, the listing page, accounts and their email. The provider
/// and organiser consoles are for adults at work and are not held to it.
/// </para>
/// <para>
/// Flesch-Kincaid on a three-word label is noise, so each string is held to grade 8 once it has
/// ten words, and the youth-facing text as a whole is held to it too. Syllables are counted by
/// the usual vowel-group rule, which is within a syllable or so of a dictionary on ordinary words.
/// Arguments are read as a short word, and a plural as its <c>other</c> form.
/// </para>
/// </remarks>
public sealed partial class ReadingLevelTests
{
    private const double Grade = 8.0;

    private static readonly string[] YouthFacing =
        ["site.", "home.", "finder.", "listing.", "account.", "email.", "form.", "collections.", "hoods.", "org.", "saved.", "map."];

    private static readonly Catalogue Shipped = Catalogue.Load(typeof(Catalogue).Assembly);

    private static IEnumerable<(string Key, string Text)> Strings() =>
        Shipped.Keys
            .Where(k => YouthFacing.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .Select(k => (k, Shipped.TryGet(Locale.English.Code, k, out _, out string s) ? Plain(s) : ""));

    [Fact]
    public void Every_youth_facing_sentence_reads_at_grade_eight_or_below()
    {
        string[] hard =
        [
            .. Strings()
                .Where(s => Words(s.Text).Count >= 10)
                .Select(s => (s.Key, Grade: FleschKincaid(s.Text)))
                .Where(s => s.Grade > Grade)
                .Select(s => $"{s.Key}: grade {s.Grade:0.0}"),
        ];

        hard.ShouldBeEmpty();
    }

    [Fact]
    public void The_youth_facing_text_as_a_whole_reads_at_grade_eight_or_below()
    {
        string all = string.Join(" ", Strings().Select(s => s.Text.TrimEnd() + (s.Text.TrimEnd().EndsWith('.') ? "" : ".")));

        FleschKincaid(all).ShouldBeLessThanOrEqualTo(Grade);
    }

    [Theory]
    [InlineData("The cat sat on the mat.", -5, 2)]
    [InlineData("Participation requires documentation of eligibility and authorisation.", 12, 30)]
    public void The_measure_tells_easy_from_hard(string text, double min, double max) =>
        FleschKincaid(text).ShouldBeInRange(min, max);

    internal static double FleschKincaid(string text)
    {
        List<string> words = Words(text);
        int sentences = Math.Max(1, SentenceBreak().Split(text).Count(s => !string.IsNullOrWhiteSpace(s)));
        int syllables = words.Sum(Syllables);
        return words.Count == 0 ? 0 : (0.39 * words.Count / sentences) + (11.8 * syllables / words.Count) - 15.59;
    }

    private static List<string> Words(string text) => [.. Word().Matches(text).Select(m => m.Value)];

    internal static int Syllables(string word)
    {
        string w = NonLetters().Replace(word.ToLowerInvariant(), "");
        if (w.Length == 0)
        {
            return 0;
        }

        if (w.Length <= 3)
        {
            return 1;
        }

        w = SilentEnding().Replace(w, "");
        w = w.TrimStart('y');
        return Math.Max(1, VowelGroup().Count(w));
    }

    /// <summary>A string as a reader meets it: plurals as their "other" form, arguments as a short word.</summary>
    private static string Plain(string template)
    {
        string text = PluralOther().Replace(template, m => m.Groups[1].Value);
        return Argument().Replace(text.Replace('#', '3'), "it");
    }

    [GeneratedRegex(@"[.!?:;]+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[A-Za-z][A-Za-z'’-]*")]
    private static partial Regex Word();

    [GeneratedRegex("[^a-z]")]
    private static partial Regex NonLetters();

    [GeneratedRegex("(?:[^laeiouy]es|ed|[^laeiouy]e)$")]
    private static partial Regex SilentEnding();

    [GeneratedRegex("[aeiouy]{1,2}")]
    private static partial Regex VowelGroup();

    [GeneratedRegex(@"\{\w+, (?:plural|select),.*?other \{([^{}]*)\}\}")]
    private static partial Regex PluralOther();

    [GeneratedRegex(@"\{\w+\}")]
    private static partial Regex Argument();
}
