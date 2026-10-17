using System.Text;

namespace BrightTime.Web.Finder;

/// <summary>
/// The Markdown an organiser may write on a neighbourhood page: paragraphs, headings
/// (<c>#</c>, <c>##</c>), bulleted lists (<c>-</c> or <c>*</c>), <c>**strong**</c>,
/// <c>*emphasis*</c>, and <c>[links](https://…)</c>. Nothing else.
/// </summary>
/// <remarks>
/// It parses into blocks and runs, and the page renders those with ordinary encoded Razor, so
/// no markup string is ever emitted: a <c>&lt;script&gt;</c> in the source is text. A link is
/// kept only to an <c>http(s)</c>, <c>mailto:</c> or <c>tel:</c> address or a path on this site;
/// anything else (<c>javascript:</c>) is shown as its words.
/// </remarks>
public static class PlainMarkdown
{
    public abstract record Block;

    public sealed record Heading(int Level, IReadOnlyList<Run> Runs) : Block;

    public sealed record Paragraph(IReadOnlyList<Run> Runs) : Block;

    public sealed record BulletList(IReadOnlyList<IReadOnlyList<Run>> Items) : Block;

    public abstract record Run;

    public sealed record TextRun(string Text) : Run;

    public sealed record StrongRun(string Text) : Run;

    public sealed record EmphasisRun(string Text) : Run;

    public sealed record LinkRun(string Text, string Href) : Run;

    public static IReadOnlyList<Block> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        List<Block> blocks = [];
        string normalised = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string chunk in normalised.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] lines = [.. chunk.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
            if (lines.Length == 1 && lines[0].StartsWith('#'))
            {
                int hashes = lines[0].TakeWhile(c => c == '#').Count();
                blocks.Add(new Heading(hashes == 1 ? 3 : 4, Runs(lines[0][hashes..].Trim())));
            }
            else if (lines.All(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal)))
            {
                blocks.Add(new BulletList([.. lines.Select(l => Runs(l[2..].Trim()))]));
            }
            else
            {
                blocks.Add(new Paragraph(Runs(string.Join(' ', lines))));
            }
        }

        return blocks;
    }

    public static IReadOnlyList<Run> Runs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Run> runs = [];
        StringBuilder plain = new();

        void Flush()
        {
            if (plain.Length > 0)
            {
                runs.Add(new TextRun(plain.ToString()));
                plain.Clear();
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            if (text.AsSpan(i).StartsWith("**") && Close(text, i + 2, "**") is int strongEnd and > 0)
            {
                Flush();
                runs.Add(new StrongRun(text[(i + 2)..strongEnd]));
                i = strongEnd + 2;
            }
            else if (text[i] is '*' or '_' && Close(text, i + 1, text[i].ToString()) is int emEnd and > 0)
            {
                Flush();
                runs.Add(new EmphasisRun(text[(i + 1)..emEnd]));
                i = emEnd + 1;
            }
            else if (text[i] == '[' && Close(text, i + 1, "](") is int labelEnd and > 0 && Close(text, labelEnd + 2, ")") is int hrefEnd and > 0)
            {
                Flush();
                string label = text[(i + 1)..labelEnd];
                string href = text[(labelEnd + 2)..hrefEnd].Trim();
                runs.Add(IsSafe(href) ? new LinkRun(label, href) : new TextRun(label));
                i = hrefEnd + 1;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }

        Flush();
        return runs;
    }

    public static bool IsSafe(string href)
    {
        ArgumentNullException.ThrowIfNull(href);

        // A path on this site first: on Unix, "/collections" also parses as an absolute file: URI.
        if (href.StartsWith('/'))
        {
            return !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\', StringComparison.Ordinal);
        }

        return Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" or "mailto" or "tel";
    }

    /// <summary>Where <paramref name="marker"/> next appears with something before it, or -1.</summary>
    private static int Close(string text, int from, string marker)
    {
        int at = text.IndexOf(marker, from, StringComparison.Ordinal);
        return at > from ? at : -1;
    }
}
