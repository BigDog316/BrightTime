using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Enrollment.Forms;

/// <summary>A choice in a single- or multi-choice question.</summary>
public sealed record FormChoice(string Key, IReadOnlyDictionary<string, string> Label);

/// <summary>
/// One question on a registration form. <see cref="Label"/> and <see cref="Help"/> are keyed by
/// locale; English is required, the other supported locales optional, and a family sees its own
/// language where the provider wrote one.
/// </summary>
public sealed record FormQuestion(
    string Key,
    string Kind,
    IReadOnlyDictionary<string, string> Label,
    IReadOnlyDictionary<string, string>? Help = null,
    bool Required = false,
    bool Sensitive = false,
    IReadOnlyList<FormChoice>? Choices = null,
    int? MaxLength = null,
    decimal? Min = null,
    decimal? Max = null);

/// <summary>
/// The provider-authored shape of a registration form (<c>form_definition.schema_json</c>, D45).
/// </summary>
/// <remarks>
/// <para>
/// Checked whole before it is stored: keys an answer row can be filed under
/// (<c>application_answer.question_key</c>), kinds the app knows how to draw, labels in supported
/// languages, and limits that make sense. Nothing a provider writes is ever run.
/// </para>
/// <para>
/// <see cref="FormQuestion.Sensitive"/> marks a question whose answers are excluded from every report, export
/// and aggregate (<c>application_answer.is_sensitive</c>): income, immigration status, disability
/// detail, race or ethnicity collected for a funder.
/// </para>
/// </remarks>
public sealed partial record FormSchema(IReadOnlyList<FormQuestion> Questions)
{
    public const int MaxQuestions = 60;
    public const int MaxChoices = 30;
    public const int MaxLabelLength = 300;
    public const int MaxHelpLength = 1000;
    public const int MaxTextLength = 5000;

    public static class Kinds
    {
        public const string ShortText = "short_text";
        public const string LongText = "long_text";
        public const string Number = "number";
        public const string Date = "date";
        public const string YesNo = "yes_no";
        public const string SingleChoice = "single_choice";
        public const string MultiChoice = "multi_choice";

        /// <summary>A statement the family must tick to submit, such as a photo release.</summary>
        public const string Consent = "consent";

        public static IReadOnlySet<string> All { get; } = new HashSet<string>(
            [ShortText, LongText, Number, Date, YesNo, SingleChoice, MultiChoice, Consent], StringComparer.Ordinal);

        public static bool IsChoice(string kind) => kind is SingleChoice or MultiChoice;

        public static bool IsText(string kind) => kind is ShortText or LongText;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Every problem with the schema, or none; the first one is what a provider is told.</summary>
    public Error? Check()
    {
        if (Questions is not { Count: > 0 and <= MaxQuestions })
        {
            return Error.Validation("form.questions", $"A form has 1 to {MaxQuestions} questions.");
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int i = 0; i < Questions.Count; i++)
        {
            FormQuestion q = Questions[i];
            Dictionary<string, object> at = new(StringComparer.Ordinal) { ["question"] = i + 1 };
            if (q is null || q.Key is null || !QuestionKey().IsMatch(q.Key) || !keys.Add(q.Key))
            {
                return Error.Validation("form.question_key", "Each question needs its own key: lower-case letters, digits and underscores, starting with a letter, at most 40.", arguments: at);
            }

            if (q.Kind is null || !Kinds.All.Contains(q.Kind))
            {
                return Error.Validation("form.question_kind", "One of " + string.Join(", ", Kinds.All.Order(StringComparer.Ordinal)) + ".", arguments: at);
            }

            if (!Words(q.Label, MaxLabelLength, required: true) || (q.Help is not null && !Words(q.Help, MaxHelpLength, required: false)))
            {
                return Error.Validation("form.question_label", $"An English label of at most {MaxLabelLength} characters, other languages optional, help of at most {MaxHelpLength}.", arguments: at);
            }

            if (Kinds.IsChoice(q.Kind) != q.Choices is not null)
            {
                return Error.Validation("form.question_choices", "Choices belong on choice questions, and only there.", arguments: at);
            }

            if (q.Choices is { } choices)
            {
                HashSet<string> choiceKeys = new(StringComparer.Ordinal);
                if (choices.Count is < 2 or > MaxChoices
                    || choices.Any(c => c is null || c.Key is null || !QuestionKey().IsMatch(c.Key) || !choiceKeys.Add(c.Key) || !Words(c.Label, MaxLabelLength, required: true)))
                {
                    return Error.Validation("form.question_choices", $"2 to {MaxChoices} choices, each with its own key and an English label.", arguments: at);
                }
            }

            bool limitsFit = q.Kind switch
            {
                _ when Kinds.IsText(q.Kind) => q.MaxLength is null or (>= 1 and <= MaxTextLength) && q.Min is null && q.Max is null,
                Kinds.Number => q.MaxLength is null && (q.Min is null || q.Max is null || q.Min <= q.Max),
                _ => q.MaxLength is null && q.Min is null && q.Max is null,
            };
            if (!limitsFit)
            {
                return Error.Validation("form.question_limits", "A length limit on text questions, a range on number questions, and neither elsewhere.", arguments: at);
            }

            if (q.Kind == Kinds.Consent && !q.Required)
            {
                return Error.Validation("form.question_limits", "A consent question is always required.", arguments: at);
            }
        }

        return null;
    }

    /// <summary>The schema as stored: trimmed, dictionaries in key order, so equal forms hash equal.</summary>
    public string ToCanonicalJson() => JsonSerializer.Serialize(new FormSchema([.. Questions.Select(Canonical)]), Json);

    public byte[] Hash() => SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson()));

    public static FormSchema FromJson(string json) => JsonSerializer.Deserialize<FormSchema>(json, Json)
        ?? throw new InvalidOperationException("A stored form schema is empty.");

    /// <summary>The text for <paramref name="locale"/>, or the English when the provider wrote none in it.</summary>
    public static string? In(IReadOnlyDictionary<string, string>? words, string locale) =>
        words is null ? null : words.TryGetValue(locale, out string? text) ? text : words.GetValueOrDefault(Locale.Default.Code);

    private static FormQuestion Canonical(FormQuestion q) => q with
    {
        Label = Sorted(q.Label)!,
        Help = Sorted(q.Help),
        Choices = q.Choices?.Select(c => c with { Label = Sorted(c.Label)! }).ToList(),
    };

    private static SortedDictionary<string, string>? Sorted(IReadOnlyDictionary<string, string>? words) =>
        words is null ? null : new(words.ToDictionary(w => w.Key, w => w.Value.Trim(), StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool Words(IReadOnlyDictionary<string, string>? words, int max, bool required) =>
        words is not null
        && (!required || words.ContainsKey(Locale.Default.Code))
        && words.All(w => Locale.TryFromCode(w.Key, out _)
            && w.Value?.Trim() is { Length: > 0 } text && text.Length <= max && !text.Any(c => char.IsControl(c) && c != '\n'));

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex QuestionKey();
}
