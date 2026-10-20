using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>The outcome of reading a finder URL. Either a query to run, or the fields that need fixing.</summary>
public sealed record FinderRequestParse(
    SearchQuery? Query,
    IReadOnlyDictionary<string, Phrase> FieldErrors,
    IReadOnlyList<Phrase> Notices)
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

        Dictionary<string, Phrase> errors = new(StringComparer.Ordinal);
        List<Phrase> notices = [];

        string? text = FirstValue(values, TextKey)?.Trim();
        if (text is { Length: > MaxTextLength })
        {
            errors[TextKey] = Phrase.Of("finder.error.text_too_long", ("max", MaxTextLength));
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
                notices.Add(Phrase.Of("finder.notice.unknown_kind", ("kind", kind.Length > 40 ? kind[..40] : kind)));
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
                errors[AgeKey] = Phrase.Of("finder.error.age_range", ("min", ListingFacets.MinAge), ("max", ListingFacets.MaxAge));
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
                errors[MaxDollarsKey] = Phrase.Of("finder.error.price_range", ("max", MaxDollars));
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
                errors[SortKey] = Phrase.Of("finder.error.sort");
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
                notices.Add(Phrase.Of("finder.notice.cursor_expired"));
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
    public static ListingKind? KindFromParam(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "program" => ListingKind.Program,
        "event" => ListingKind.Event,
        "job" => ListingKind.Job,
        "resource" => ListingKind.Resource,
        _ => null,
    };

    /// <summary>Accepts the sort words used in the URL.</summary>
    public static SearchSort? SortFromParam(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
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
