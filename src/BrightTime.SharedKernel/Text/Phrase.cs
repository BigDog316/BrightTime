using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BrightTime.SharedKernel.Text;

/// <summary>
/// Something to say to a person, as data: which catalogue entry, and the values that fill it.
/// </summary>
/// <remarks>
/// <para>
/// Modules return phrases rather than sentences, so they never choose a language. The host
/// renders one in the reader's locale from its catalogue (<c>BrightTime.Web/Localisation</c>),
/// where the English, and every translation, lives.
/// </para>
/// <para>
/// An argument is a number, a string, or another <see cref="Phrase"/>, which is rendered in the
/// same locale first. A status name inside a sentence ("needs a reason before it can be
/// unpublished") is a phrase, not an English word.
/// </para>
/// </remarks>
public sealed partial record Phrase
{
    private static readonly IReadOnlyDictionary<string, object> None = new Dictionary<string, object>();

    public Phrase(string key, IReadOnlyDictionary<string, object>? arguments = null)
    {
        if (!KeyPattern().IsMatch(key ?? string.Empty))
        {
            throw new ArgumentException($"'{key}' is not a catalogue key: lower case, dot-separated, a-z 0-9 and underscores.", nameof(key));
        }

        Key = key!;
        Arguments = arguments ?? None;
    }

    public string Key { get; }

    public IReadOnlyDictionary<string, object> Arguments { get; }

    public static Phrase Of(string key) => new(key);

    public static Phrase Of(string key, params ReadOnlySpan<(string Name, object Value)> arguments)
    {
        Dictionary<string, object> values = new(arguments.Length, StringComparer.Ordinal);
        foreach ((string name, object value) in arguments)
        {
            ArgumentNullException.ThrowIfNull(value, name);
            values[name] = value;
        }

        return new Phrase(key, values);
    }

    /// <summary>The key and its arguments, for logs and test output. Never shown to a person.</summary>
    public override string ToString()
    {
        if (Arguments.Count == 0)
        {
            return Key;
        }

        StringBuilder text = new(Key);
        text.Append('(');
        text.AppendJoin(", ", Arguments.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Key}={a.Value}")));
        return text.Append(')').ToString();
    }

    public bool Equals(Phrase? other) =>
        other is not null
        && Key == other.Key
        && Arguments.Count == other.Arguments.Count
        && Arguments.All(a => other.Arguments.TryGetValue(a.Key, out object? v) && Equals(a.Value, v));

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9_]+(\\.[a-z0-9_]+)+$")]
    private static partial Regex KeyPattern();
}
