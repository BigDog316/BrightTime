using System.Collections.Frozen;
using BrightTime.Modules.Platform.Localisation;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.Caching.Memory;

namespace BrightTime.Web.Localisation;

/// <summary>A tenant's usable overrides, parsed: by locale, then key.</summary>
public sealed class WordingSet
{
    public static readonly WordingSet Empty = new(FrozenDictionary<(string, string), MessageTemplate>.Empty);

    private readonly FrozenDictionary<(string Locale, string Key), MessageTemplate> _templates;

    public WordingSet(FrozenDictionary<(string Locale, string Key), MessageTemplate> templates) => _templates = templates;

    public int Count => _templates.Count;

    public bool TryGet(string locale, string key, out MessageTemplate template) =>
        _templates.TryGetValue((locale, key), out template!);
}

/// <summary>The request tenant's wording overrides.</summary>
public interface IWordingOverrides
{
    ValueTask<WordingSet> ForTenantAsync(TenantId tenant, CancellationToken cancellationToken = default);

    /// <summary>After a change, so this instance shows it at once. Other instances catch up when their cache expires.</summary>
    void Invalidate(TenantId tenant);
}

/// <summary>Sample data has no database, so no overrides.</summary>
public sealed class NoWordingOverrides : IWordingOverrides
{
    public ValueTask<WordingSet> ForTenantAsync(TenantId tenant, CancellationToken cancellationToken = default) => ValueTask.FromResult(WordingSet.Empty);

    public void Invalidate(TenantId tenant)
    {
    }
}

/// <summary>
/// Overrides from <c>platform.resource_override</c>, cached per tenant for five minutes: the
/// table is read on a page's first string, and changes rarely.
/// </summary>
/// <remarks>
/// An override that no longer fits its string (the English gained an argument since it was
/// saved) is left out and logged, rather than rendering a broken sentence.
/// </remarks>
public sealed partial class SqlWordingOverrides(ResourceOverrides store, Catalogue catalogue, IMemoryCache cache, ILogger<SqlWordingOverrides> logger) : IWordingOverrides
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async ValueTask<WordingSet> ForTenantAsync(TenantId tenant, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey(tenant), out WordingSet? cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<WordingOverride> rows = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<(string, string), MessageTemplate> templates = [];
        foreach (WordingOverride row in rows)
        {
            if (!Locale.TryFromCode(row.Locale, out Locale locale) || catalogue.Check(locale, row.Key, row.Value).Count > 0)
            {
                LogUnusable(logger, row.Locale, row.Key);
                continue;
            }

            templates[(locale.Code, row.Key)] = MessageTemplate.Parse(row.Value);
        }

        WordingSet set = templates.Count == 0 ? WordingSet.Empty : new WordingSet(templates.ToFrozenDictionary());
        cache.Set(CacheKey(tenant), set, Lifetime);
        return set;
    }

    public void Invalidate(TenantId tenant) => cache.Remove(CacheKey(tenant));

    private static string CacheKey(TenantId tenant) => "wording:" + tenant.Value.ToString("N");

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "The wording override for {Key} in {Locale} no longer fits its string and is not used.")]
    private static partial void LogUnusable(ILogger logger, string locale, string key);
}
