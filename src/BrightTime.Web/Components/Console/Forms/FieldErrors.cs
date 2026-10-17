using BrightTime.SharedKernel.Text;

namespace BrightTime.Web.Components.Console.Forms;

/// <summary>A form's field errors, keyed by field name, and the aria wiring that goes with them.</summary>
public sealed class FieldErrors(IReadOnlyDictionary<string, Phrase[]> errors)
{
    public static readonly FieldErrors None = new(new Dictionary<string, Phrase[]>());

    public IReadOnlyDictionary<string, Phrase[]> All => errors;

    public int Count => errors.Count;

    public bool Has(string field) => errors.ContainsKey(field);

    public Phrase[] For(string field) => errors.TryGetValue(field, out Phrase[]? messages) ? messages : [];

    public string? Invalid(string field) => Has(field) ? "true" : null;

    public string? DescribedBy(string field, string? hintId = null) =>
        (hintId, Has(field) ? $"err-{field}" : null) switch
        {
            (null, null) => null,
            (null, { } e) => e,
            ({ } h, null) => h,
            ({ } h, { } e) => $"{h} {e}",
        };
}
