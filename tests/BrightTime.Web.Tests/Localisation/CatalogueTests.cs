using System.Text.RegularExpressions;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Localisation;

namespace BrightTime.Web.Tests.Localisation;

/// <summary>
/// The shipped catalogue, held to its contract: every locale complete, every translation using
/// the same arguments as the English and the plural forms its language needs, and every key the
/// code asks for present (and every key present asked for). A gap here is a failed build, not
/// English in a Bengali page.
/// </summary>
public sealed partial class CatalogueTests
{
    private static readonly Catalogue Shipped = Catalogue.Load(typeof(Catalogue).Assembly);

    public static TheoryData<string> Translations() => [.. Locale.Supported.Where(l => l != Locale.English).Select(l => l.Code)];

    [Fact]
    public void There_is_a_catalogue_for_each_launch_locale_and_every_string_parses()
    {
        Shipped.LocaleCodes.Order(StringComparer.Ordinal).ShouldBe(Locale.Supported.Select(l => l.Code).Order(StringComparer.Ordinal));
        Shipped.LoadProblems.ShouldBeEmpty();
        Shipped.Keys.Count.ShouldBeGreaterThan(400);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_has_exactly_the_english_keys(string locale)
    {
        string[] missing = [.. Shipped.Keys.Except(Shipped.KeysOf(locale), StringComparer.Ordinal)];
        string[] extra = [.. Shipped.KeysOf(locale).Except(Shipped.Keys, StringComparer.Ordinal)];

        missing.ShouldBeEmpty($"{locale} is missing strings");
        extra.ShouldBeEmpty($"{locale} has strings English does not");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_uses_the_same_arguments_and_the_plural_forms_its_language_needs(string locale)
    {
        Locale target = Locale.TryFromCode(locale, out Locale l) ? l : throw new InvalidOperationException(locale);
        List<string> problems = [];
        foreach (string key in Shipped.KeysOf(locale))
        {
            Shipped.TryGet(locale, key, out _, out string source).ShouldBeTrue();
            problems.AddRange(Shipped.Check(target, key, source).Select(p => $"{key}: {p}"));
        }

        problems.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_says_who_has_checked_it(string locale)
    {
        Shipped.Metadata[locale]["locale"].ShouldBe(locale);
        Shipped.Metadata[locale].ShouldContainKey("review");
    }

    [Fact]
    public void No_string_points_a_direction_that_flips_in_arabic()
    {
        // An arrow that means "next" in English means "back" in a right-to-left page.
        char[] arrows = ['→', '←', '⇒', '⇐', '▶', '◀', '►', '◄'];
        List<string> offenders = [];
        foreach (string locale in Shipped.LocaleCodes)
        {
            foreach (string key in Shipped.KeysOf(locale))
            {
                Shipped.TryGet(locale, key, out _, out string source);
                if (source.IndexOfAny(arrows) >= 0)
                {
                    offenders.Add($"{locale}: {key}");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Every_key_the_code_asks_for_exists()
    {
        (HashSet<string> literals, HashSet<string> _) = Usage();

        literals.Where(k => !Shipped.Has(k)).Order(StringComparer.Ordinal).ShouldBeEmpty();
    }

    [Fact]
    public void Every_string_in_the_catalogue_is_used()
    {
        (HashSet<string> literals, HashSet<string> prefixes) = Usage();

        Shipped.Keys
            .Where(k => !literals.Contains(k) && !prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .ShouldBeEmpty("a string nothing renders is a string a translator is paid to translate for nothing");
    }

    [Fact]
    public void Every_module_error_a_person_can_be_shown_has_a_translation()
    {
        HashSet<string> codes = [];
        foreach (string file in SourceFiles())
        {
            foreach (Match m in ErrorCode().Matches(File.ReadAllText(file)))
            {
                codes.Add("error." + m.Groups[1].Value);
            }
        }

        codes.Count.ShouldBeGreaterThan(20);
        codes.Where(c => !Shipped.Has(c)).Order(StringComparer.Ordinal).ShouldBeEmpty();
    }

    /// <summary>
    /// Keys in the source: literal keys in a localising call, and the prefixes of keys built at
    /// run time (<c>"listing.kind." + kind</c>). Audit actions and error codes share the dotted
    /// shape but are not catalogue keys, so only literals in a localising context count.
    /// </summary>
    private static (HashSet<string> Literals, HashSet<string> Prefixes) Usage()
    {
        HashSet<string> literals = new(StringComparer.Ordinal);
        HashSet<string> prefixes = new(StringComparer.Ordinal) { "error." };
        foreach (string file in SourceFiles())
        {
            foreach (string line in File.ReadLines(file))
            {
                if (!LocalisingCall().IsMatch(line))
                {
                    continue;
                }

                foreach (Match m in KeyLiteral().Matches(line))
                {
                    string value = m.Groups[1].Value;
                    if (value.EndsWith('.') || value.EndsWith('_'))
                    {
                        prefixes.Add(value);
                    }
                    else
                    {
                        literals.Add(value);
                    }
                }
            }
        }

        return (literals, prefixes);
    }

    private static IEnumerable<string> SourceFiles()
    {
        string src = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BrightTime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }

    [GeneratedRegex(@"\b(T|text|t)\[|\b(T|text|t)\.(Format|Has)\(|(?<![\w.])Format\(""|Phrase\.Of\(")]
    private static partial Regex LocalisingCall();

    [GeneratedRegex(@"""([a-z0-9_-]+(?:\.[a-z0-9_-]+)*[._]|[a-z0-9_]+(?:\.[a-z0-9_-]+)+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"Error\.(?:Validation|Conflict|Forbidden|NotFound|Precondition)\(\s*""([a-z_.]+)""")]
    private static partial Regex ErrorCode();
}
