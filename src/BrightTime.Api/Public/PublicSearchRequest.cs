using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Api.Public;

/// <summary>A search to run, or what is wrong with the request, keyed by parameter.</summary>
public sealed record PublicSearchParse(
    SearchQuery? Query,
    IReadOnlyDictionary<string, string[]> Errors,
    IReadOnlyList<Phrase> Notices);

/// <summary>
/// Reads <c>GET /api/v1/public/listings</c>: the canonical filter vocabulary of docs/plan/05.
/// </summary>
/// <remarks>
/// <para>
/// Supported: <c>q, kind[], category[], accommodation[], age, grade, from, to, free, maxCost,
/// mealsProvided, transportProvided, dropIn, nearNeighbourhood, orgId, sort, cursor, limit</c>.
/// Repeated values of <c>category</c> match any of them; repeated <c>accommodation</c> values
/// must all be met. <c>maxCost</c> is whole dollars, as on the web.
/// </para>
/// <para>
/// Not yet: <c>interest, language, dayOfWeek, modality</c> (the query has one any-of list,
/// which <c>category</c> uses) and <c>nearAddress, nearSchool, radiusM</c> (geocoding is open
/// question Q7). Unknown parameters are ignored, as the plan's additive versioning expects.
/// </para>
/// <para>
/// Strict about values: a malformed number or date is an error, never coerced. Lenient about
/// shape: an unknown kind or a stale cursor is dropped and reported as a notice.
/// </para>
/// </remarks>
public static partial class PublicSearchRequest
{
    public const int MaxTextLength = FinderRequestParser.MaxTextLength;
    public const int MaxDollars = FinderRequestParser.MaxDollars;
    public const int MaxCodes = 20;

    /// <param name="values">The query string, each key with its values.</param>
    /// <param name="tenantId">The request's tenant, which every query is scoped to.</param>
    /// <param name="locale">The request's language, which picks the text-search configuration.</param>
    /// <param name="neighbourhoodId">Resolves a neighbourhood code; null when there is no such code.</param>
    public static PublicSearchParse Parse(
        IReadOnlyDictionary<string, string[]> values,
        Guid tenantId,
        Locale locale,
        Func<string, Guid?> neighbourhoodId)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(neighbourhoodId);

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);
        List<Phrase> notices = [];
        List<string> all = [];
        List<string> any = [];

        string? text = First(values, "q")?.Trim();
        if (text is { Length: > MaxTextLength })
        {
            errors["q"] = [$"At most {MaxTextLength} characters."];
        }

        List<string> kinds = [];
        foreach (string kind in All(values, "kind"))
        {
            if (FinderRequestParser.KindFromParam(kind) is { } known)
            {
                kinds.Add(known.ToDbValue());
            }
            else
            {
                notices.Add(Phrase.Of("finder.notice.unknown_kind", ("kind", kind.Length > 40 ? kind[..40] : kind)));
            }
        }

        Codes(values, "category", "cat:", any, errors);
        Codes(values, "accommodation", "accom:", all, errors);

        if (Whole(values, "age", ListingFacets.MinAge, ListingFacets.MaxAge, errors) is { } age)
        {
            all.Add("age:" + age.ToString(CultureInfo.InvariantCulture));
        }

        if (Whole(values, "grade", ListingFacets.MinGrade, ListingFacets.MaxGrade, errors) is { } grade)
        {
            all.Add("grade:" + grade.ToString(CultureInfo.InvariantCulture));
        }

        bool freeOnly = Flag(values, "free", errors);
        if (Flag(values, "mealsProvided", errors))
        {
            all.Add("meals");
        }

        if (Flag(values, "transportProvided", errors))
        {
            all.Add("transport");
        }

        if (Flag(values, "dropIn", errors))
        {
            all.Add("dropin");
        }

        int? maxCostCents = Whole(values, "maxCost", 0, MaxDollars, errors) * 100;

        if (First(values, "nearNeighbourhood") is { Length: > 0 } code)
        {
            if (neighbourhoodId(code.Trim()) is { } hood)
            {
                all.Add(ListingFacets.NeighbourhoodToken(hood));
            }
            else
            {
                errors["nearNeighbourhood"] = ["No neighbourhood has that code."];
            }
        }

        Guid? organisation = null;
        if (First(values, "orgId") is { Length: > 0 } orgText)
        {
            if (Guid.TryParse(orgText, out Guid org))
            {
                organisation = org;
            }
            else
            {
                errors["orgId"] = ["Not an id."];
            }
        }

        DateTimeOffset? from = Date(values, "from", errors);
        DateTimeOffset? to = Date(values, "to", errors);
        if (from is { } f && to is { } t && t < f)
        {
            errors["to"] = ["Before from."];
        }

        SearchSort sort = SearchSort.Relevance;
        if (First(values, "sort") is { Length: > 0 } sortText)
        {
            if (FinderRequestParser.SortFromParam(sortText.Trim()) is { } parsed)
            {
                sort = parsed;
            }
            else
            {
                errors["sort"] = ["One of relevance, soonest, nearest, newest."];
            }
        }

        Cursor? after = null;
        if (First(values, "cursor") is { Length: > 0 } cursorText)
        {
            if (Cursor.TryDecode(cursorText, out Cursor decoded))
            {
                after = decoded;
            }
            else
            {
                notices.Add(Phrase.Of("finder.notice.cursor_expired"));
            }
        }

        int limit = Whole(values, "limit", 1, PageSize.Max, errors) ?? PageSize.Default;

        if (errors.Count > 0)
        {
            return new PublicSearchParse(null, errors, notices);
        }

        return new PublicSearchParse(
            new SearchQuery
            {
                TenantId = tenantId,
                Text = string.IsNullOrWhiteSpace(text) ? null : text,
                TextSearchConfig = locale.TextSearchConfig,
                AllFacets = all.Count == 0 ? null : all,
                AnyFacets = any.Count == 0 ? null : any,
                Kinds = kinds.Count == 0 ? null : kinds,
                OrganisationId = organisation,
                WindowStart = from,
                WindowEnd = to,
                FreeOnly = freeOnly,
                MaxCostCents = maxCostCents,
                Sort = sort,
                After = after,
                PageSize = new PageSize(limit),
            },
            errors,
            notices);
    }

    private static string? First(IReadOnlyDictionary<string, string[]> values, string key) =>
        values.TryGetValue(key, out string[]? found) && found.Length > 0 ? found[0] : null;

    /// <summary>Repeated keys, and the bracketed form some clients send (<c>kind[]=</c>).</summary>
    private static IEnumerable<string> All(IReadOnlyDictionary<string, string[]> values, string key) =>
        new[] { key, key + "[]" }
            .SelectMany(k => values.TryGetValue(k, out string[]? found) ? found : [])
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void Codes(IReadOnlyDictionary<string, string[]> values, string key, string prefix, List<string> into, Dictionary<string, string[]> errors)
    {
        List<string> codes = [.. All(values, key).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (codes.Count > MaxCodes)
        {
            errors[key] = [$"At most {MaxCodes} values."];
            return;
        }

        // Taxonomy codes are lower-case slugs (D26); anything else cannot match, so say so.
        foreach (string code in codes)
        {
            if (code.Length > 64 || !code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                errors[key] = ["Codes are lower-case letters, digits, '-' and '_'."];
                return;
            }

            into.Add(prefix + code);
        }
    }

    private static int? Whole(IReadOnlyDictionary<string, string[]> values, string key, int min, int max, Dictionary<string, string[]> errors)
    {
        string? text = First(values, key)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max)
        {
            return value;
        }

        errors[key] = [$"A whole number from {min} to {max}."];
        return null;
    }

    private static bool Flag(IReadOnlyDictionary<string, string[]> values, string key, Dictionary<string, string[]> errors)
    {
        string? text = First(values, key)?.Trim();
        switch (text?.ToUpperInvariant())
        {
            case null or "":
                return false;
            case "TRUE" or "1":
                return true;
            case "FALSE" or "0":
                return false;
            default:
                errors[key] = ["true or false."];
                return false;
        }
    }

    private static DateTimeOffset? Date(IReadOnlyDictionary<string, string[]> values, string key, Dictionary<string, string[]> errors)
    {
        string? text = First(values, key)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // An instant with its offset, so "from" never depends on the server's time zone.
        if (Offset().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset value))
        {
            return value.ToUniversalTime();
        }

        errors[key] = ["An ISO 8601 date and time with an offset, such as 2026-11-01T00:00:00-04:00."];
        return null;
    }

    /// <summary>ISO 8601 date and time that states its offset.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial System.Text.RegularExpressions.Regex Offset();
}
