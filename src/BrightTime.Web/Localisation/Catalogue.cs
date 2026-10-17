using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Web.Localisation;

/// <summary>
/// The interface's strings, one flat JSON file per locale in <c>Localisation/Strings</c>,
/// embedded in the assembly.
/// </summary>
/// <remarks>
/// <para>
/// Keys are stable names (<c>finder.results.count</c>), never the English text, so a tenant's
/// override and every translation survive an edit to the English. Values are
/// <see cref="MessageTemplate"/>s. Entries starting with <c>@@</c> are metadata, as in ARB:
/// <c>@@locale</c>, and <c>@@review</c> saying who has checked the translation.
/// </para>
/// <para>
/// English is complete by definition. Every other locale must have exactly English's keys, with
/// the same arguments and the plural forms its language needs; the catalogue tests hold them to
/// that, so a missing translation is a failed build rather than English in a Bengali page.
/// At run time a gap still falls back to English rather than showing a key.
/// </para>
/// </remarks>
public sealed class Catalogue
{
    public const string ResourcePrefix = "BrightTime.Strings.";

    private readonly FrozenDictionary<string, FrozenDictionary<string, Entry>> _byLocale;

    private Catalogue(Dictionary<string, Dictionary<string, Entry>> byLocale, Dictionary<string, IReadOnlyDictionary<string, string>> metadata, List<string> loadProblems)
    {
        _byLocale = byLocale.ToFrozenDictionary(l => l.Key, l => l.Value.ToFrozenDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
        Metadata = metadata;
        LoadProblems = loadProblems;
        Keys = _byLocale.TryGetValue(Locale.English.Code, out FrozenDictionary<string, Entry>? english)
            ? [.. english.Keys.Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>English's keys: the complete set.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Each locale's <c>@@</c> entries.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Metadata { get; }

    /// <summary>Strings that did not parse when loaded. They are left out and fall back to English.</summary>
    public IReadOnlyList<string> LoadProblems { get; }

    public static Catalogue Load() => Load(typeof(Catalogue).Assembly);

    public static Catalogue Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Dictionary<string, string> sources = new(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream);
            sources[name[ResourcePrefix.Length..^".json".Length]] = reader.ReadToEnd();
        }

        return FromJson(sources);
    }

    /// <summary>Builds a catalogue from locale code → JSON text. Tests use this directly.</summary>
    public static Catalogue FromJson(IReadOnlyDictionary<string, string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dictionary<string, Dictionary<string, Entry>> byLocale = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyDictionary<string, string>> metadata = new(StringComparer.Ordinal);
        List<string> problems = [];

        foreach ((string code, string json) in sources)
        {
            Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
            Dictionary<string, string> meta = new(StringComparer.Ordinal);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"{code}: {property.Name} is not a string");
                    continue;
                }

                string value = property.Value.GetString()!;
                if (property.Name.StartsWith("@@", StringComparison.Ordinal))
                {
                    meta[property.Name[2..]] = value;
                    continue;
                }

                if (entries.ContainsKey(property.Name))
                {
                    problems.Add($"{code}: {property.Name}: appears twice");
                }
                else if (MessageTemplate.TryParse(value, out MessageTemplate? template, out string? problem))
                {
                    entries[property.Name] = new Entry(value, template!);
                }
                else
                {
                    problems.Add($"{code}: {property.Name}: {problem}");
                }
            }

            byLocale[code] = entries;
            metadata[code] = meta;
        }

        return new Catalogue(byLocale, metadata, problems);
    }

    public IEnumerable<string> LocaleCodes => _byLocale.Keys;

    public bool Has(string key) => _byLocale.TryGetValue(Locale.English.Code, out FrozenDictionary<string, Entry>? en) && en.ContainsKey(key);

    /// <summary>The template for a key in a locale, without falling back.</summary>
    public bool TryGet(string locale, string key, out MessageTemplate template, out string source)
    {
        if (_byLocale.TryGetValue(locale, out FrozenDictionary<string, Entry>? entries) && entries.TryGetValue(key, out Entry? entry))
        {
            template = entry.Template;
            source = entry.Source;
            return true;
        }

        template = null!;
        source = string.Empty;
        return false;
    }

    public IReadOnlyCollection<string> KeysOf(string locale) =>
        _byLocale.TryGetValue(locale, out FrozenDictionary<string, Entry>? entries) ? entries.Keys : [];

    /// <summary>
    /// What would be wrong with <paramref name="candidate"/> as the text for <paramref name="key"/>
    /// in <paramref name="locale"/>, compared with English: a parse error, an argument missing or
    /// made up, a plural form the language needs and the text lacks. Empty when it is usable.
    /// </summary>
    public IReadOnlyList<string> Check(Locale locale, string key, string candidate)
    {
        ArgumentNullException.ThrowIfNull(locale);
        List<string> problems = [];
        if (!TryGet(Locale.English.Code, key, out MessageTemplate english, out _))
        {
            problems.Add($"there is no string called {key}");
            return problems;
        }

        if (!MessageTemplate.TryParse(candidate, out MessageTemplate? parsed, out string? parseProblem))
        {
            problems.Add(parseProblem!);
            return problems;
        }

        foreach ((string name, ArgumentUse use) in english.Arguments)
        {
            if (!parsed!.Arguments.TryGetValue(name, out ArgumentUse? theirs))
            {
                problems.Add($"{{{name}}} is missing");
                continue;
            }

            if (use.Kind == ArgumentKind.Plural)
            {
                if (theirs.Kind != ArgumentKind.Plural)
                {
                    problems.Add($"{{{name}}} must be a plural, as in English");
                    continue;
                }

                foreach (PluralCategory needed in PluralRules.Required(locale.Code))
                {
                    if (!theirs.Branches.Contains(PluralRules.Name(needed), StringComparer.Ordinal) && !CoveredExactly(needed, theirs.Branches))
                    {
                        problems.Add($"{{{name}}} needs a '{PluralRules.Name(needed)}' form in {locale.EnglishName}");
                    }
                }
            }
            else if (use.Kind == ArgumentKind.Select)
            {
                foreach (string branch in use.Branches.Where(b => !theirs.Branches.Contains(b, StringComparer.Ordinal)))
                {
                    problems.Add($"{{{name}}} is missing its '{branch}' choice");
                }
            }
        }

        foreach (string extra in parsed!.Arguments.Keys.Where(n => !english.Arguments.ContainsKey(n)))
        {
            problems.Add($"{{{extra}}} is not something this string is given");
        }

        return problems;
    }

    /// <summary>Arabic's zero, one and two are single numbers, so =0, =1 and =2 cover them.</summary>
    private static bool CoveredExactly(PluralCategory category, IReadOnlyList<string> branches) => category switch
    {
        PluralCategory.Zero => branches.Contains("=0", StringComparer.Ordinal),
        PluralCategory.One => branches.Contains("=1", StringComparer.Ordinal),
        PluralCategory.Two => branches.Contains("=2", StringComparer.Ordinal),
        _ => false,
    };

    private sealed record Entry(string Source, MessageTemplate Template);
}
