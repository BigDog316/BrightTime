using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Notifications;

/// <summary>A notification in words: what a push shows and what the inbox lists.</summary>
public sealed record NotificationWords(string Title, string Body);

/// <summary>
/// Puts a notification into words. A request names a key under <c>notify.</c>; its title and body
/// are that key's <c>.title</c> and <c>.body</c> in the shared catalogues, in the reader's language.
/// </summary>
/// <remarks>
/// Arguments travel as strings. An instant (an argument named <c>at</c> or ending in <c>_at</c>,
/// in ISO 8601) is written as a day and time in the tenant's zone; anything else is a name, and
/// is isolated so a right-to-left title cannot reorder the sentence around it.
/// </remarks>
public static class NotificationText
{
    public const string KeyPrefix = "notify.";

    public static NotificationWords Render(Text text, string key, IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A notification key starts with '{KeyPrefix}'.", nameof(key));
        }

        string what = key[KeyPrefix.Length..];
        (string Name, object Value)[] values = [.. arguments.Select(a => (a.Key, Value(text, a.Key, a.Value)))];
        return new NotificationWords(
            text.Format("notify." + what + ".title", values),
            text.Format("notify." + what + ".body", values));
    }

    private static string Value(Text text, string name, string value) =>
        (name == "at" || name.EndsWith("_at", StringComparison.Ordinal))
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
            ? text.DayTime(at)
            : Text.Name(value);
}

/// <summary>
/// <see cref="Text"/> for the current tenant in a language of the caller's choosing: a push goes out
/// in the language its phone was registered in, whoever's job is sending it.
/// </summary>
public sealed class NotificationTextFactory(Catalogue catalogue, IWordingOverrides overrides, ILogger<Text> logger, ITenantContext tenant)
{
    private readonly Dictionary<Locale, Text> _loaded = [];

    /// <summary>Text in <paramref name="locale"/> when it is a supported language, else in the tenant's own.</summary>
    public async ValueTask<Text> ForAsync(string? locale, CancellationToken cancellationToken = default)
    {
        Locale language = Locale.TryFromCode(locale, out Locale chosen) ? chosen : tenant.Locale;
        if (!_loaded.TryGetValue(language, out Text? text))
        {
            text = new Text(catalogue, new Context(tenant, language), overrides, logger);
            await text.LoadAsync(cancellationToken).ConfigureAwait(false);
            _loaded[language] = text;
        }

        return text;
    }

    private sealed class Context(ITenantContext tenant, Locale locale) : ITextContext
    {
        public TenantId TenantId => tenant.TenantId;

        public bool IsResolved => tenant.IsResolved;

        public Locale Locale => locale;

        public TimeZoneInfo TimeZone => tenant.TimeZone;
    }
}
