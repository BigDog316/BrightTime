using System.Text.RegularExpressions;

namespace BrightTime.Web.Tests.Localisation;

/// <summary>
/// Arabic mirrors because the styles never say left or right: margin-inline-start, not
/// margin-left. A physical property in any stylesheet would stay put while the page flips.
/// </summary>
public sealed partial class RightToLeftTests
{
    [Fact]
    public void No_stylesheet_uses_a_physical_direction()
    {
        string src = Path.Combine(RepositoryRoot(), "src");
        string[] offenders =
        [
            .. Directory.EnumerateFiles(src, "*.css", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
                .Where(l => Physical().IsMatch(l.Text))
                .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}"),
        ];

        offenders.ShouldBeEmpty();
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BrightTime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }

    [GeneratedRegex(@"(?<![-\w])(margin|padding|border)-(left|right)\b|(?<![-\w])(left|right)\s*:|text-align\s*:\s*(left|right)|float\s*:\s*(left|right)|clear\s*:\s*(left|right)")]
    private static partial Regex Physical();
}
