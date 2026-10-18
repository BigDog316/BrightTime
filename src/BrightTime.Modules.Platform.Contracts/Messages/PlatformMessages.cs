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
