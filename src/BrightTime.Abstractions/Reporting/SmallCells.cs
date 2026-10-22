namespace BrightTime.Abstractions.Reporting;

/// <summary>
/// What a report hid, and why: the envelope docs/plan/05 puts on every aggregate. Saying that
/// figures were hidden, and how many, is part of the report; numbers that silently go missing teach
/// readers to mistrust them.
/// </summary>
public sealed record SuppressionSummary(int MinCellSize, int SuppressedCells, bool Complementary, string Policy);

/// <summary>
/// Small-cell suppression for a table of counts (docs/plan/07, T17), the same rule as
/// <c>analytics.suppress()</c>: zero is published, 1 to threshold−1 is hidden. Complementary
/// suppression then hides one more figure in any line that has a published total and exactly one
/// hidden figure, so the hidden one can't be found by subtracting.
/// </summary>
public static class SmallCells
{
    /// <summary>The value, or null when it is a small count; zero is published.</summary>
    public static long? Suppress(long value, int threshold) =>
        value == 0 ? 0 : value < threshold ? null : value;

    /// <summary>
    /// Suppresses a column of counts that is shown with its total, which is itself suppressed if it
    /// is small. Returns the figures and how many were hidden. Ties break toward the earlier row, so
    /// the same data always hides the same cells.
    /// </summary>
    public static (long?[] Cells, long? Total, int Hidden) Column(IReadOnlyList<long> cells, long total, int threshold, bool complementary = true)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);

        long?[] shown = [.. cells.Select(c => Suppress(c, threshold))];
        long? shownTotal = Suppress(total, threshold);
        if (complementary && shownTotal is not null)
        {
            Complement(cells, shown);
        }

        int hidden = shown.Count(c => c is null) + (shownTotal is null ? 1 : 0);
        return (shown, shownTotal, hidden);
    }

    /// <summary>With exactly one hidden figure in the line, also hides the smallest published non-zero one.</summary>
    private static void Complement(IReadOnlyList<long> cells, long?[] shown)
    {
        if (shown.Count(c => c is null) != 1)
        {
            return;
        }

        int smallest = -1;
        for (int i = 0; i < shown.Length; i++)
        {
            if (shown[i] is > 0 && (smallest < 0 || cells[i] < cells[smallest]))
            {
                smallest = i;
            }
        }

        if (smallest >= 0)
        {
            shown[smallest] = null;
        }
    }
}
