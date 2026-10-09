namespace BrightTime.SharedKernel.Paging;

/// <summary>A page of results plus the cursor that fetches the next one.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public static Page<T> Empty { get; } = new([], null);

    public bool HasMore => NextCursor is not null;

    public int Count => Items.Count;
}

/// <summary>A validated page-size request. Clamped, because an unbounded limit is a denial-of-service vector on an anonymous endpoint.</summary>
public readonly record struct PageSize
{
    public const int Default = 24;
    public const int Max = 100;

    public PageSize(int requested) => Value = Math.Clamp(requested <= 0 ? Default : requested, 1, Max);

    public int Value { get; }

    /// <summary>One more than the page size, so a query can detect "there is a next page" without a second COUNT.</summary>
    public int FetchCount => Value + 1;

    public static implicit operator int(PageSize size) => size.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
