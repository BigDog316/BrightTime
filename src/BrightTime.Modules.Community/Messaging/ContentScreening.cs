using System.Diagnostics;
using System.Text.Json;
using BrightTime.Abstractions.Screening;
using BrightTime.Modules.Community.Persistence;

namespace BrightTime.Modules.Community.Messaging;

/// <summary>Which screen something written passes before anyone else sees it.</summary>
internal enum MessageScreen
{
    /// <summary>Between adults: none.</summary>
    None,

    /// <summary>An adult's writing to young people: an attempt to take it off the platform (D53).</summary>
    ContactExchange,

    /// <summary>A young person's own writing: personal information (D117).</summary>
    YouthPii,
}

/// <summary>
/// A screen's verdict on one piece of writing: held or not, why, where in the text, and how long it
/// took. Whatever isn't a pass is held for a moderator, a redaction or a block too (D117).
/// </summary>
internal sealed record ScreenVerdict(string Decision, IReadOnlyList<string> Reasons, IReadOnlyList<PiiSpan> Spans, string? Version, int? LatencyMs, string Provider)
{
    public bool Passed => Decision == "pass";

    /// <summary>The content's own <c>screen_result</c>: <c>{"reasons": [...]}</c>, which the moderator and the author read.</summary>
    public string ScreenResult => JsonSerializer.Serialize(new { reasons = Reasons });

    /// <summary>When and why, as its own record (D116): the moderation history reads the hold from here.</summary>
    public ScreeningResultRecord Record(Guid tenant, string targetKind, Guid target, DateTimeOffset at) => new()
    {
        Id = SharedKernel.Ids.UuidV7.NewId(),
        TenantId = tenant,
        TargetKind = targetKind,
        TargetId = target,
        Provider = Provider,
        Decision = Decision,
        Labels = JsonSerializer.Serialize(Reasons.ToDictionary(r => r, _ => 1.0)),
        PiiSpans = JsonSerializer.Serialize(Spans.Select(s => new { start = s.Start, length = s.Length, kind = s.Kind })),
        ModelVersion = Version,
        LatencyMs = LatencyMs,
        CreatedAt = at,
    };
}

/// <summary>The screens a message (D53, D117) and a post (D120) pass, in one place.</summary>
internal static class ContentScreening
{
    /// <summary>The reason something is held when its screen failed (D117): it fails closed.</summary>
    public const string ScreeningError = "screening_error";

    /// <summary>The screen's verdict, or null when there is no screen to pass.</summary>
    public static async Task<ScreenVerdict?> ScreenAsync(IScreeningService screener, string text, MessageScreen screen, bool audienceIncludesMinor, string surface, CancellationToken ct)
    {
        if (screen == MessageScreen.None)
        {
            return null;
        }

        if (screen == MessageScreen.ContactExchange)
        {
            IReadOnlyList<string> found = ContactExchangeScreen.Reasons(text);
            return new ScreenVerdict(found.Count > 0 ? "hold" : "pass", found, [], null, null, ContactExchangeScreen.Provider);
        }

        long started = Stopwatch.GetTimestamp();
        ScreeningOutcome outcome;
        try
        {
            outcome = await screener.ScreenTextAsync(text, new ScreeningContext(
                AuthorIsMinor: true, AudienceIncludesMinor: audienceIncludesMinor, AuthorIsAdultAddressingMinor: false, Surface: surface), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Fails closed: a screen that breaks holds what it was screening.
            outcome = new ScreeningOutcome(ScreeningDecision.Error, [], new Dictionary<string, double>(StringComparer.Ordinal), null, e.GetType().Name);
        }

        IReadOnlyList<string> reasons = outcome.Decision switch
        {
            ScreeningDecision.Pass => [],
            ScreeningDecision.Error => [ScreeningError],
            _ => outcome.Labels.Count > 0 ? [.. outcome.Labels.Keys] : [ScreeningError],
        };
        return new ScreenVerdict(outcome.Decision.ToString().ToLowerInvariant(), reasons, outcome.PiiSpans, outcome.ModelVersion,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, Infrastructure.Screening.LocalPiiScreen.Provider);
    }
}
