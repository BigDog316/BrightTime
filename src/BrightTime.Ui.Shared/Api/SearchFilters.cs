using System.Globalization;
using System.Text;

namespace BrightTime.Ui.Shared.Api;

/// <summary>
/// A search, as the filter sheet sets it: the subset of <c>GET /public/listings</c>'s
/// parameters the app offers (docs/plan/05). Empty values are left out of the query.
/// </summary>
public sealed record SearchFilters
{
    public const string Relevance = "relevance";
    public const string Soonest = "soonest";
    public const string Newest = "newest";

    public string? Text { get; init; }

    /// <summary>Listing kinds as the API names them: <c>program</c>, <c>event</c>, <c>job</c>, <c>resource</c>.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Category codes; a listing in any of them matches.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    public int? Age { get; init; }

    public bool FreeOnly { get; init; }

    /// <summary>Highest price, in whole dollars.</summary>
    public int? MaxCostDollars { get; init; }

    public bool MealsProvided { get; init; }

    public bool TransportProvided { get; init; }

    public bool DropIn { get; init; }

    /// <summary>A neighbourhood code: listings in or near it. The device's position never leaves it.</summary>
    public string? NearNeighbourhood { get; init; }

    public string Sort { get; init; } = Relevance;

    public static SearchFilters StartingSoon { get; } = new() { Sort = Soonest };

    /// <summary>How many filters are set, for the filter button's badge. The text and sort are not filters.</summary>
    public int Count =>
        Kinds.Count + Categories.Count + (Age is null ? 0 : 1) + (FreeOnly ? 1 : 0) + (MaxCostDollars is null ? 0 : 1)
        + (MealsProvided ? 1 : 0) + (TransportProvided ? 1 : 0) + (DropIn ? 1 : 0) + (NearNeighbourhood is null ? 0 : 1);

    /// <summary>The query string, without the leading <c>?</c>.</summary>
    public string ToQuery(string? cursor = null, int? limit = null)
    {
        StringBuilder query = new();
        Add("q", string.IsNullOrWhiteSpace(Text) ? null : Text.Trim());
        foreach (string kind in Kinds)
        {
            Add("kind", kind);
        }

        foreach (string category in Categories)
        {
            Add("category", category);
        }

        Add("age", Age?.ToString(CultureInfo.InvariantCulture));
        Add("free", FreeOnly ? "true" : null);
        Add("maxCost", MaxCostDollars?.ToString(CultureInfo.InvariantCulture));
        Add("mealsProvided", MealsProvided ? "true" : null);
        Add("transportProvided", TransportProvided ? "true" : null);
        Add("dropIn", DropIn ? "true" : null);
        Add("nearNeighbourhood", NearNeighbourhood);
        Add("sort", Sort == Relevance ? null : Sort);
        Add("cursor", cursor);
        Add("limit", limit?.ToString(CultureInfo.InvariantCulture));
        return query.ToString();

        void Add(string name, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }
}
