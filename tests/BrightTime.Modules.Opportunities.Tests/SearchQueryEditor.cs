using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Paging;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>A mutable builder for test queries, so each test states only what it changes.</summary>
internal sealed class SearchQueryEditor
{
    public Guid TenantId { get; set; }

    public string? Text { get; set; }

    public IReadOnlyList<string>? AllFacets { get; set; }

    public IReadOnlyList<string>? Kinds { get; set; }

    public DateTimeOffset? WindowStart { get; set; }

    public DateTimeOffset? WindowEnd { get; set; }

    public int? MaxCostCents { get; set; }

    public bool FreeOnly { get; set; }

    public IReadOnlyList<string>? InterestTokens { get; set; }

    public SearchSort Sort { get; set; } = SearchSort.Relevance;

    public Cursor? After { get; set; }

    public int PageSizeValue { get; set; } = PageSize.Default;

    public SearchQuery Build() => new()
    {
        TenantId = TenantId,
        Text = Text,
        AllFacets = AllFacets,
        Kinds = Kinds,
        WindowStart = WindowStart,
        WindowEnd = WindowEnd,
        MaxCostCents = MaxCostCents,
        FreeOnly = FreeOnly,
        InterestTokens = InterestTokens,
        Sort = Sort,
        After = After,
        PageSize = new PageSize(PageSizeValue),
    };
}
