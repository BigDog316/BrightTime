using System.Text.RegularExpressions;
using BrightTime.Abstractions.Screening;

namespace BrightTime.Modules.Community.Screening;

/// <summary>
/// Screens youth-authored text for personal information, by rules, on our own servers (Q8's "local
/// PII detection always", D117): a phone number, an email address, a street address, a social
/// handle, or a link. Anything found holds the text for a moderator, with each span's offsets and
/// kind; nothing leaves the platform, and no model reads it.
/// </summary>
/// <remarks>
/// <para>
/// It errs towards holding, as the contact-exchange screen does: a false match costs the young
/// person a short wait and a rewrite, a miss puts their details in front of someone. Year ranges
/// ("2025-2026") aren't phone numbers. A street address is a house number and a street word within a
/// few words of it ("12 Elm Street", "4 on Main St"), in English only.
/// </para>
/// <para>
/// Abuse classification isn't here: it waits for a hosted provider and a data-processing agreement
/// (Q8). Fails closed: an error is <see cref="ScreeningDecision.Error"/>, which holds.
/// </para>
/// </remarks>
public sealed partial class LocalPiiScreen : IScreeningService
{
    /// <summary>The screen's name in <c>screening_result.provider</c>.</summary>
    public const string Provider = "local_pii";

    /// <summary>The rules' version, in <c>screening_result.model_version</c>.</summary>
    public const string Version = "local-pii-1";

    public const string Phone = "phone";
    public const string Email = "email";
    public const string Address = "address";
    public const string Handle = "handle";
    public const string Link = "link";

    public Task<ScreeningOutcome> ScreenTextAsync(string text, ScreeningContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            IReadOnlyList<PiiSpan> spans = Find(text);
            Dictionary<string, double> labels = spans.Select(s => s.Kind).Distinct(StringComparer.Ordinal).ToDictionary(k => k, _ => 1.0, StringComparer.Ordinal);
            return Task.FromResult(new ScreeningOutcome(
                spans.Count == 0 ? ScreeningDecision.Pass : ScreeningDecision.Hold,
                spans,
                labels,
                Version,
                spans.Count == 0 ? null : "Personal information: " + string.Join(", ", labels.Keys)));
        }
        catch (RegexMatchTimeoutException)
        {
            return Task.FromResult(new ScreeningOutcome(ScreeningDecision.Error, [], new Dictionary<string, double>(StringComparer.Ordinal), Version, "The screen timed out."));
        }
    }

    /// <summary>Every span of personal information, in order, without overlaps (the first kind found wins).</summary>
    public static IReadOnlyList<PiiSpan> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<PiiSpan> found = [];
        foreach (Match m in EmailAddress().Matches(text))
        {
            found.Add(new PiiSpan(m.Index, m.Length, Email));
        }

        foreach (Match m in Url().Matches(text))
        {
            found.Add(new PiiSpan(m.Index, m.Length, Link));
        }

        foreach (Match m in PhoneNumber().Matches(text))
        {
            int digits = m.Value.Count(char.IsAsciiDigit);
            if (digits is >= 7 and <= 15 && !YearRange().IsMatch(m.Value))
            {
                found.Add(new PiiSpan(m.Index, m.Length, Phone));
            }
        }

        foreach (Match m in StreetAddress().Matches(text))
        {
            found.Add(new PiiSpan(m.Index, m.Length, Address));
        }

        foreach (Match m in SocialHandle().Matches(text))
        {
            Group name = m.Groups["name"];
            found.Add(new PiiSpan(name.Index, name.Length, Handle));
        }

        List<PiiSpan> kept = [];
        foreach (PiiSpan span in found.OrderBy(s => s.Start).ThenByDescending(s => s.Length))
        {
            if (kept.Count == 0 || span.Start >= kept[^1].Start + kept[^1].Length)
            {
                kept.Add(span);
            }
        }

        return kept;
    }

    private const int TimeoutMs = 250;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+\s*(@|\(at\)|\[at\])\s*[A-Za-z0-9.\-]+\s*(\.|\(dot\)|\[dot\])\s*[A-Za-z]{2,}", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"(https?://|www\.)\S+|\b[A-Za-z0-9\-]+\.(com|net|org|io|me|gg|app|ly)(/\S*)?\b", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex Url();

    [GeneratedRegex(@"\+?\(?\d[\d\s().\-]{5,}\d", RegexOptions.None, TimeoutMs)]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"^\s*(19|20)\d{2}\s*[-–]\s*(19|20)?\d{2}\s*$", RegexOptions.None, TimeoutMs)]
    private static partial Regex YearRange();

    [GeneratedRegex(@"\b\d{1,5}\s+(?:[A-Za-z0-9.'\-]+\s+){0,3}(?:street|st|avenue|ave|road|rd|lane|ln|drive|dr|boulevard|blvd|court|ct|place|pl|way|terrace|ter|parkway|pkwy|circle|cir|highway|hwy)\b\.?", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex StreetAddress();

    [GeneratedRegex(@"(^|[\s(])(?<name>@[A-Za-z0-9_.]{2,})", RegexOptions.None, TimeoutMs)]
    private static partial Regex SocialHandle();
}
