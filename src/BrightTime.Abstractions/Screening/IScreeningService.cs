namespace BrightTime.Abstractions.Screening;

public enum ScreeningDecision
{
    /// <summary>Publish as written.</summary>
    Pass,

    /// <summary>Publish with the detected spans removed.</summary>
    Redact,

    /// <summary>Hold for a human. The author sees it pending; nobody else sees it.</summary>
    Hold,

    /// <summary>Refuse, and tell the author why.</summary>
    Block,

    /// <summary>The screener failed. Treated as <see cref="Hold"/> — fail closed.</summary>
    Error,
}

/// <summary>A span of detected personal information, as character offsets into the submitted text.</summary>
public sealed record PiiSpan(int Start, int Length, string Kind);

public sealed record ScreeningOutcome(
    ScreeningDecision Decision,
    IReadOnlyList<PiiSpan> PiiSpans,
    IReadOnlyDictionary<string, double> Labels,
    string? ModelVersion,
    string? Explanation);

/// <summary>
/// Screens youth-authored text before anybody else can see it.
/// </summary>
/// <remarks>
/// <para>
/// Submitted content is <em>data</em>. An implementation that calls a language
/// model must pass the text in a delimited data channel under a fixed system
/// prompt, and must constrain the response to this enum plus label scores. A
/// free-text model response may never become an action: that is the difference
/// between a classifier and a confused deputy.
/// </para>
/// <para>
/// Fails closed. A screener that errors yields <see cref="ScreeningDecision.Hold"/>,
/// never <see cref="ScreeningDecision.Pass"/>.
/// </para>
/// </remarks>
public interface IScreeningService
{
    Task<ScreeningOutcome> ScreenTextAsync(
        string text,
        ScreeningContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the screener needs to know about the situation, which changes the
/// threshold: an adult writing to a minor is screened harder than a youth
/// posting in their own cohort.
/// </summary>
public sealed record ScreeningContext(
    bool AuthorIsMinor,
    bool AudienceIncludesMinor,
    bool AuthorIsAdultAddressingMinor,
    string Surface);
