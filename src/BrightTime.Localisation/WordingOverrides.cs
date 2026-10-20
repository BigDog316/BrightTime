using System.Collections.Frozen;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Localisation;

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

/// <summary>No overrides: sample data has no database, and an app without a connection has none to read.</summary>
public sealed class NoWordingOverrides : IWordingOverrides
{
    public ValueTask<WordingSet> ForTenantAsync(TenantId tenant, CancellationToken cancellationToken = default) => ValueTask.FromResult(WordingSet.Empty);

    public void Invalidate(TenantId tenant)
    {
    }
}
