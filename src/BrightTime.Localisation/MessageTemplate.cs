using System.Globalization;
using System.Text;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Localisation;

/// <summary>
/// A catalogue string, parsed: the subset of ICU MessageFormat the interface needs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>{name}</c> inserts an argument.</item>
/// <item><c>{n, plural, =0 {none} one {# result} other {# results}}</c> picks by the CLDR plural
/// category of <c>n</c> in the locale (<see cref="PluralRules"/>), or by an exact <c>=N</c>
/// match first; <c>#</c> is the number.</item>
/// <item><c>{by, select, provider {...} other {...}}</c> picks by a word.</item>
/// </list>
/// <para>
/// Braces only ever open and close an argument: there is no escaping, and a literal brace is a
/// parse error. Apostrophes are ordinary characters, unlike ICU's quoting rule, because
/// translators type them constantly ("don't", "l'activité") and ICU's rule turns them into
/// silent bugs.
/// </para>
/// </remarks>
public sealed class MessageTemplate
{
    private readonly Node[] _nodes;

    private MessageTemplate(Node[] nodes) => _nodes = nodes;

    /// <summary>Every argument the template reads, with what it expects of it.</summary>
    public IReadOnlyDictionary<string, ArgumentUse> Arguments { get; private init; } = new Dictionary<string, ArgumentUse>();

    public static MessageTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Parser parser = new(text);
        Node[] nodes = parser.ParseSequence(inPlural: false, terminator: null);
        Dictionary<string, ArgumentUse> uses = new(StringComparer.Ordinal);
        Collect(nodes, uses);
        return new MessageTemplate(nodes) { Arguments = uses };
    }

    public static bool TryParse(string text, out MessageTemplate? template, out string? problem)
    {
        try
        {
            template = Parse(text);
            problem = null;
            return true;
        }
        catch (FormatException ex)
        {
            template = null;
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>Renders with <paramref name="render"/> turning each argument value into text.</summary>
    public string Format(string locale, IReadOnlyDictionary<string, object> arguments, Func<object, string> render)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(render);
        StringBuilder output = new();
        Write(_nodes, locale, arguments, render, null, output);
        return output.ToString();
    }

    private static void Write(Node[] nodes, string locale, IReadOnlyDictionary<string, object> args, Func<object, string> render, object? pound, StringBuilder output)
    {
        foreach (Node node in nodes)
        {
            switch (node)
            {
                case TextNode t:
                    output.Append(t.Text);
                    break;

                case PoundNode:
                    output.Append(pound is null ? "#" : render(pound));
                    break;

                case ArgumentNode a:
                    output.Append(args.TryGetValue(a.Name, out object? given) ? render(given) : "{" + a.Name + "}");
                    break;

                case PluralNode p:
                    {
                        object value = args.TryGetValue(p.Name, out object? v) ? v : 0;
                        decimal number = ToDecimal(value);
                        string exact = "=" + number.ToString(CultureInfo.InvariantCulture);
                        Node[] branch = p.Branches.TryGetValue(exact, out Node[]? e)
                            ? e
                            : p.Branches.TryGetValue(PluralRules.Name(PluralRules.Select(locale, number)), out Node[]? c)
                                ? c
                                : p.Branches["other"];
                        Write(branch, locale, args, render, value, output);
                        break;
                    }

                case SelectNode s:
                    {
                        string word = args.TryGetValue(s.Name, out object? v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
                        Node[] branch = s.Branches.TryGetValue(word, out Node[]? b) ? b : s.Branches["other"];
                        Write(branch, locale, args, render, pound, output);
                        break;
                    }
            }
        }
    }

    private static decimal ToDecimal(object value) => value switch
    {
        int i => i,
        long l => l,
        short s => s,
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : 0,
    };

    private static void Collect(Node[] nodes, Dictionary<string, ArgumentUse> uses)
    {
        foreach (Node node in nodes)
        {
            switch (node)
            {
                case ArgumentNode a:
                    Merge(uses, a.Name, ArgumentUse.Simple());
                    break;
                case PluralNode p:
                    Merge(uses, p.Name, ArgumentUse.Plural([.. p.Branches.Keys]));
                    foreach (Node[] branch in p.Branches.Values)
                    {
                        Collect(branch, uses);
                    }

                    break;
                case SelectNode s:
                    Merge(uses, s.Name, ArgumentUse.Select([.. s.Branches.Keys]));
                    foreach (Node[] branch in s.Branches.Values)
                    {
                        Collect(branch, uses);
                    }

                    break;
            }
        }
    }

    private static void Merge(Dictionary<string, ArgumentUse> uses, string name, ArgumentUse use)
    {
        if (!uses.TryGetValue(name, out ArgumentUse? existing))
        {
            uses[name] = use;
            return;
        }

        // An argument used both plainly and as a plural is a plural; its branches are unioned.
        uses[name] = existing.Kind == ArgumentKind.Simple ? use
            : use.Kind == ArgumentKind.Simple ? existing
            : existing with { Branches = [.. existing.Branches.Union(use.Branches, StringComparer.Ordinal)] };
    }

    private abstract record Node;

    private sealed record TextNode(string Text) : Node;

    private sealed record PoundNode : Node;

    private sealed record ArgumentNode(string Name) : Node;

    private sealed record PluralNode(string Name, Dictionary<string, Node[]> Branches) : Node;

    private sealed record SelectNode(string Name, Dictionary<string, Node[]> Branches) : Node;

    private sealed class Parser(string text)
    {
        private int _at;

        public Node[] ParseSequence(bool inPlural, char? terminator)
        {
            List<Node> nodes = [];
            StringBuilder literal = new();

            void Flush()
            {
                if (literal.Length > 0)
                {
                    nodes.Add(new TextNode(literal.ToString()));
                    literal.Clear();
                }
            }

            while (_at < text.Length)
            {
                char c = text[_at];
                if (terminator is { } end && c == end)
                {
                    Flush();
                    return [.. nodes];
                }

                if (c == '{')
                {
                    Flush();
                    nodes.Add(ParseArgument());
                    continue;
                }

                if (c == '}')
                {
                    throw Problem("a '}' with no '{' before it");
                }

                if (c == '#' && inPlural)
                {
                    Flush();
                    nodes.Add(new PoundNode());
                    _at++;
                    continue;
                }

                literal.Append(c);
                _at++;
            }

            if (terminator is not null)
            {
                throw Problem("a '{' that is never closed");
            }

            Flush();
            return [.. nodes];
        }

        private Node ParseArgument()
        {
            _at++; // '{'
            string name = ReadWord("an argument name");
            SkipSpace();
            if (Peek() == '}')
            {
                _at++;
                return new ArgumentNode(name);
            }

            Expect(',');
            SkipSpace();
            string type = ReadWord("plural or select");
            SkipSpace();
            Expect(',');
            Dictionary<string, Node[]> branches = new(StringComparer.Ordinal);
            bool plural = type switch
            {
                "plural" => true,
                "select" => false,
                _ => throw Problem($"'{type}' is not plural or select"),
            };

            while (true)
            {
                SkipSpace();
                if (Peek() == '}')
                {
                    _at++;
                    break;
                }

                string selector = ReadSelector();
                if (plural && !selector.StartsWith('=') && !PluralRules.TryParse(selector, out _))
                {
                    throw Problem($"'{selector}' is not a plural category (zero, one, two, few, many, other) or =N");
                }

                if (!branches.TryAdd(selector, []))
                {
                    throw Problem($"'{selector}' appears twice");
                }

                SkipSpace();
                Expect('{');
                branches[selector] = ParseSequence(inPlural: plural, terminator: '}');
                _at++; // the branch's '}'
            }

            if (!branches.ContainsKey("other"))
            {
                throw Problem($"{{{name}, {type}}} needs an 'other' branch");
            }

            return plural ? new PluralNode(name, branches) : new SelectNode(name, branches);
        }

        private string ReadWord(string what)
        {
            SkipSpace();
            int start = _at;
            while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] == '_'))
            {
                _at++;
            }

            return _at > start ? text[start.._at] : throw Problem($"expected {what}");
        }

        private string ReadSelector()
        {
            int start = _at;
            while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] is '_' or '='))
            {
                _at++;
            }

            return _at > start ? text[start.._at] : throw Problem("expected a branch name");
        }

        private void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }

        private char? Peek() => _at < text.Length ? text[_at] : null;

        private void Expect(char c)
        {
            if (Peek() != c)
            {
                throw Problem($"expected '{c}'");
            }

            _at++;
        }

        private FormatException Problem(string what) => new($"{what}, at character {_at + 1} of \"{text}\"");
    }
}

public enum ArgumentKind
{
    Simple,
    Plural,
    Select,
}

/// <summary>How a template uses an argument: its kind and, for plural and select, its branches.</summary>
public sealed record ArgumentUse(ArgumentKind Kind, IReadOnlyList<string> Branches)
{
    public static ArgumentUse Simple() => new(ArgumentKind.Simple, []);

    public static ArgumentUse Plural(IReadOnlyList<string> branches) => new(ArgumentKind.Plural, branches);

    public static ArgumentUse Select(IReadOnlyList<string> branches) => new(ArgumentKind.Select, branches);
}
