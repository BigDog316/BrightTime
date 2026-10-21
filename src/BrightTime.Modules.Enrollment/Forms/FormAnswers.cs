using System.Globalization;
using System.Text.Json;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Enrollment.Forms;

/// <summary>
/// Answers checked against the form they answer (D46): the right shape for each kind, within the
/// question's limits, and complete before submitting.
/// </summary>
public static class FormAnswers
{
    public const int DefaultShortLength = 500;

    /// <summary>The first problem with <paramref name="answers"/>, or none. Null values are removals and always fit.</summary>
    public static Error? Check(FormSchema form, IReadOnlyDictionary<string, JsonElement> answers)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(answers);
        Dictionary<string, FormQuestion> questions = form.Questions.ToDictionary(q => q.Key, StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in answers)
        {
            if (!questions.TryGetValue(key, out FormQuestion? question))
            {
                return Error.Validation("application.answer_key", "That form asks no such question.", arguments: At(key));
            }

            if (value.ValueKind != JsonValueKind.Null && !Fits(question, value))
            {
                return Error.Validation("application.answer", "An answer that suits the question.", arguments: At(key));
            }
        }

        return null;
    }

    /// <summary>The first required question without an answer, a required consent not given, or none.</summary>
    public static Error? CheckComplete(FormSchema form, IReadOnlyDictionary<string, JsonElement> answers)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(answers);
        foreach (FormQuestion question in form.Questions.Where(q => q.Required))
        {
            bool answered = answers.TryGetValue(question.Key, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                && (question.Kind != FormSchema.Kinds.Consent || value.ValueKind == JsonValueKind.True);
            if (!answered)
            {
                return Error.Validation("application.answer_missing", "Answer every required question.", arguments: At(question.Key));
            }
        }

        return null;
    }

    /// <summary>
    /// The normalised row for reporting. A sensitive answer's row says only that it was answered:
    /// the value itself stays sealed in the snapshot.
    /// </summary>
    public static ApplicationAnswerRecord Row(Guid application, FormQuestion question, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (question.Sensitive)
        {
            return new ApplicationAnswerRecord { ApplicationId = application, QuestionKey = question.Key, IsSensitive = true };
        }

        return question.Kind switch
        {
            FormSchema.Kinds.Number => new() { ApplicationId = application, QuestionKey = question.Key, ValueNumber = value.GetDecimal() },
            FormSchema.Kinds.Date => new() { ApplicationId = application, QuestionKey = question.Key, ValueDate = Date(value) },
            FormSchema.Kinds.YesNo or FormSchema.Kinds.Consent => new() { ApplicationId = application, QuestionKey = question.Key, ValueBool = value.GetBoolean() },
            FormSchema.Kinds.MultiChoice => new() { ApplicationId = application, QuestionKey = question.Key, ValueChoices = [.. value.EnumerateArray().Select(v => v.GetString()!)] },
            _ => new() { ApplicationId = application, QuestionKey = question.Key, ValueText = value.GetString()!.Trim() },
        };
    }

    private static bool Fits(FormQuestion q, JsonElement value)
    {
        switch (q.Kind)
        {
            case FormSchema.Kinds.ShortText or FormSchema.Kinds.LongText:
                int max = q.MaxLength ?? (q.Kind == FormSchema.Kinds.ShortText ? DefaultShortLength : FormSchema.MaxTextLength);
                return value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text && text.Length <= max
                    && !text.Any(c => char.IsControl(c) && !(q.Kind == FormSchema.Kinds.LongText && c is '\n' or '\r'));
            case FormSchema.Kinds.Number:
                return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal n)
                    && (q.Min is null || n >= q.Min) && (q.Max is null || n <= q.Max);
            case FormSchema.Kinds.Date:
                return value.ValueKind == JsonValueKind.String && Date(value) is not null;
            case FormSchema.Kinds.YesNo or FormSchema.Kinds.Consent:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case FormSchema.Kinds.SingleChoice:
                return value.ValueKind == JsonValueKind.String && q.Choices!.Any(c => c.Key == value.GetString());
            case FormSchema.Kinds.MultiChoice:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                {
                    return false;
                }

                List<string?> picked = [.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null)];
                return picked.Distinct().Count() == picked.Count && picked.All(p => q.Choices!.Any(c => c.Key == p));
            default:
                return false;
        }
    }

    private static DateOnly? Date(JsonElement value) =>
        DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : null;

    private static Dictionary<string, object> At(string key) => new(StringComparer.Ordinal) { ["question"] = key };
}
