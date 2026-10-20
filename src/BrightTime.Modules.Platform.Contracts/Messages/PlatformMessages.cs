using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Modules.Platform.Contracts.Messages;

/// <summary>
/// A taxonomy term was renamed, moved, retired or restored (D26). Modules that copy a term's
/// name (the listing search rows) refresh it. Not published for a new term: nothing uses it yet.
/// </summary>
public sealed record TaxonomyTermChanged(
    Guid TermId,
    string Vocabulary,
    string Code,
    bool IsActive,
    DateTimeOffset ChangedAt) : IModuleMessage
{
    public static string MessageType => "platform.taxonomy_term.changed";
}

/// <summary>
/// Something should tell a person something. Any module may publish this (it lives here, at the
/// bottom of the module graph, so none has to reference Notifications); the Notifications module
/// decides the channels from the person's preferences, quiet hours and capabilities.
/// </summary>
/// <param name="UserId">Who is told.</param>
/// <param name="Kind">One of <c>ck_notification_kind</c>, which their preferences are kept by.</param>
/// <param name="TitleKey">A catalogue key, rendered in the recipient's language: never prose.</param>
/// <param name="Arguments">The key's arguments: identifiers, counts and dates, never personal details.</param>
/// <param name="DeepLink">An app path to open (<c>/listings/{slug}</c>), never an external address.</param>
/// <param name="DedupeKey">Two requests with the same key make one notification, so a retried job never notifies twice.</param>
/// <param name="ExpiresAt">When it stops mattering (a reminder after its deadline), and leaves the inbox.</param>
public sealed record NotificationRequested(
    Guid UserId,
    string Kind,
    string TitleKey,
    IReadOnlyDictionary<string, string> Arguments,
    string? DeepLink,
    string DedupeKey,
    DateTimeOffset? ExpiresAt = null) : IModuleMessage
{
    public static string MessageType => "platform.notification.requested";
}
