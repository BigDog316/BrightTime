using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>The outcome of reading a finder URL. Either a query to run, or the fields that need fixing.</summary>
public sealed record FinderRequestParse(
    SearchQuery? Query,
    IReadOnlyDictionary<string, string> FieldErrors,
    IReadOnlyList<string> Notices)
{
    public bool IsValid => Query is not null && FieldErrors.Count == 0;
}

/// <summary>
/// Turns the finder's query string into a <see cref="SearchQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lenient about shape, strict about values. An unknown kind is ignored and
/// reported in <see cref="FinderRequestParse.Notices"/>, so the family can see
/// that a filter was dropped rather than wonder why results changed. A malformed
/// number is a field error, shown beside the field. Nothing is coerced silently.
/// </para>
/// <para>
/// The parser never reads a home address, a school, or any other personal value.
/// The only inputs are the search text, the listing facets the family chose, and
/// the page cursor.
/// </para>
/// </remarks>
public static class FinderRequestParser
{
    public const string TextKey = "q";
    public const string KindKey = "kind";
    public const string AgeKey = "age";
    public const string FreeKey = "free";
    public const string MaxDollarsKey = "maxDollars";
    public const string SortKey = "sort";
    public const string AfterKey = "after";

    public const int MaxTextLength = 200;
    public const int MaxDollars = 100_000;

    public static FinderRequestParse Parse(
        IReadOnlyDictionary<string, string[]> values,
        Guid tenantId,
        Locale locale,
        IReadOnlyList<string>? interestTokens = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(locale);

        Dictionary<string, string> errors = new(StringComparer.Ordinal);
        List<string> notices = [];

        string? text = FirstValue(values, TextKey)?.Trim();
        if (text is { Length: > MaxTextLength })
        {
            errors[TextKey] = $"Keep the search to {MaxTextLength} characters.";
        }

        List<string> allFacets = [];
        List<string> kinds = [];
        foreach (string kind in ValuesOf(values, KindKey))
        {
            if (KindFromParam(kind) is { } known)
            {
                kinds.Add(known.ToDbValue());
            }
            else
            {
                notices.Add($"'{kind}' is not a kind of listing, so it was ignored.");
            }
        }

        string? ageText = FirstValue(values, AgeKey)?.Trim();
        if (!string.IsNullOrEmpty(ageText))
        {
            if (int.TryParse(ageText, NumberStyles.None, CultureInfo.InvariantCulture, out int age)
                && age is >= ListingFacets.MinAge and <= ListingFacets.MaxAge)
            {
                allFacets.Add("age:" + age.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                errors[AgeKey] = $"Enter an age from {ListingFacets.MinAge} to {ListingFacets.MaxAge}.";
            }
        }

        bool freeOnly = IsTruthy(FirstValue(values, FreeKey));

        int? maxCostCents = null;
        string? maxDollarsText = FirstValue(values, MaxDollarsKey)?.Trim();
        if (!string.IsNullOrEmpty(maxDollarsText))
        {
            if (int.TryParse(maxDollarsText, NumberStyles.None, CultureInfo.InvariantCulture, out int dollars)
                && dollars is >= 0 and <= MaxDollars)
            {
                maxCostCents = dollars * 100;
            }
            else
            {
                errors[MaxDollarsKey] = $"Enter a price from $0 to ${MaxDollars:N0}.";
            }
        }

        SearchSort sort = SearchSort.Relevance;
        string? sortText = FirstValue(values, SortKey)?.Trim();
        if (!string.IsNullOrEmpty(sortText))
        {
            if (SortFromParam(sortText) is { } parsedSort)
            {
                sort = parsedSort;
            }
            else
            {
                errors[SortKey] = "Choose relevance, soonest, nearest or newest.";
            }
        }

        Cursor? after = null;
        string? afterText = FirstValue(values, AfterKey);
        if (!string.IsNullOrEmpty(afterText))
        {
            if (Cursor.TryDecode(afterText, out Cursor decoded))
            {
                after = decoded;
            }
            else
            {
                // A stale or edited link. Start over rather than fail the page.
                notices.Add("That page link has expired, so you are back at the first page.");
            }
        }

        if (errors.Count > 0)
        {
            return new FinderRequestParse(null, errors, notices);
        }

        SearchQuery query = new()
        {
            TenantId = tenantId,
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            TextSearchConfig = locale.TextSearchConfig,
            AllFacets = allFacets.Count == 0 ? null : allFacets,
            Kinds = kinds.Count == 0 ? null : kinds,
            FreeOnly = freeOnly,
            MaxCostCents = maxCostCents,
            InterestTokens = interestTokens is { Count: > 0 } ? interestTokens : null,
            Sort = sort,
            After = after,
            PageSize = new PageSize(PageSize.Default),
        };

        return new FinderRequestParse(query, errors, notices);
    }

    /// <summary>Accepts the kind words used in the URL, which are the database values.</summary>
    internal static ListingKind? KindFromParam(string value) => value.Trim().ToLowerInvariant() switch
    {
        "program" => ListingKind.Program,
        "event" => ListingKind.Event,
        "job" => ListingKind.Job,
        "resource" => ListingKind.Resource,
        _ => null,
    };

    internal static SearchSort? SortFromParam(string value) => value.Trim().ToLowerInvariant() switch
    {
        "relevance" => SearchSort.Relevance,
        "soonest" => SearchSort.Soonest,
        "nearest" => SearchSort.Nearest,
        "newest" => SearchSort.Newest,
        _ => null,
    };

    private static bool IsTruthy(string? value) =>
        value is "1" or "true" or "on" or "yes";

    private static string? FirstValue(IReadOnlyDictionary<string, string[]> values, string key) =>
        values.TryGetValue(key, out string[]? found) && found.Length > 0 ? found[0] : null;

    private static IEnumerable<string> ValuesOf(IReadOnlyDictionary<string, string[]> values, string key) =>
        values.TryGetValue(key, out string[]? found)
            ? found.Where(v => !string.IsNullOrWhiteSpace(v))
            : [];
}
