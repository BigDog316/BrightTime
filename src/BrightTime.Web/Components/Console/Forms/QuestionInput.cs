using System.Globalization;
using System.Text;
using BrightTime.Modules.Enrollment.Forms;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Web.Components.Console.Forms;

/// <summary>
/// Reads a question from the form builder's fields (D62). A provider writes words, not keys: a
/// question's key and its choices' keys come from the English, and stay as they were when a
/// question is edited, so answers already given keep their meaning. FormSchema checks the rest.
/// </summary>
public static class QuestionInput
{
    /// <summary>The locales a question may be written in besides English.</summary>
    public static IReadOnlyList<Locale> Others { get; } = [.. Locale.Supported.Where(l => l.Code != Locale.Default.Code)];

    public static FormQuestion Read(Func<string, string> field, FormQuestion? existing, IEnumerable<string> takenKeys)
    {
        ArgumentNullException.ThrowIfNull(field);
        string kind = field("kind");
        string label = field("label").Trim();
        Dictionary<string, string> labels = Words(field, "label", label);
        string help = field("help").Trim();
        Dictionary<string, string>? helps = help.Length == 0 ? null : new(StringComparer.Ordinal) { [Locale.Default.Code] = help };

        IReadOnlyList<FormChoice>? choices = FormSchema.Kinds.IsChoice(kind) ? Choices(field("choices"), existing?.Choices) : null;
        bool text = FormSchema.Kinds.IsText(kind);
        bool number = kind == FormSchema.Kinds.Number;
        return new FormQuestion(
            existing?.Key ?? Key(label, takenKeys, "q"),
            kind,
            labels,
            helps,
            Required: field("required") == "true" || kind == FormSchema.Kinds.Consent,
            Sensitive: field("sensitive") == "true",
            Choices: choices,
            MaxLength: text ? Int(field("maxLength")) : null,
            Min: number ? Dec(field("min")) : null,
            Max: number ? Dec(field("max")) : null);
    }

    /// <summary>The English label, with each other language written for it.</summary>
    private static Dictionary<string, string> Words(Func<string, string> field, string name, string english)
    {
        Dictionary<string, string> words = new(StringComparer.Ordinal) { [Locale.Default.Code] = english };
        foreach (Locale locale in Others)
        {
            string text = field($"{name}-{locale.Code}").Trim();
            if (text.Length > 0)
            {
                words[locale.Code] = text;
            }
        }

        return words;
    }

    /// <summary>One choice per line; a line matching an existing choice keeps that choice's key.</summary>
    public static IReadOnlyList<FormChoice> Choices(string lines, IReadOnlyList<FormChoice>? existing)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<FormChoice> choices = [];
        foreach (string line in lines.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            FormChoice? kept = existing?.FirstOrDefault(c => c.Label.TryGetValue(Locale.Default.Code, out string? en) && en == line);
            Dictionary<string, string> label = kept is null
                ? new(StringComparer.Ordinal) { [Locale.Default.Code] = line }
                : new(kept.Label.Where(l => l.Key != Locale.Default.Code), StringComparer.Ordinal) { [Locale.Default.Code] = line };
            choices.Add(new FormChoice(kept?.Key ?? Key(line, choices.Select(c => c.Key), "choice"), label));
        }

        return choices;
    }

    /// <summary>
    /// A key from words: lower-case letters, digits and underscores, starting with a letter, at most
    /// 40, and not one already taken. Words with no Latin letters give <paramref name="fallback"/>.
    /// </summary>
    public static string Key(string words, IEnumerable<string> taken, string fallback)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(fallback);
        HashSet<string> used = new(taken, StringComparer.Ordinal);
        StringBuilder key = new();
        foreach (char c in words.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                key.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && key.Length > 0 && key[^1] != '_')
            {
                key.Append('_');
            }
        }

        string stem = key.ToString().Trim('_');
        stem = stem.Length == 0 || !char.IsAsciiLetter(stem[0]) ? fallback : stem;
        stem = stem.Length > 34 ? stem[..34].TrimEnd('_') : stem;
        string candidate = stem;
        for (int n = 2; used.Contains(candidate); n++)
        {
            candidate = $"{stem}_{n.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    private static int? Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static decimal? Dec(string text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal n) ? n : null;
}
