using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>One concrete meeting of a session, in absolute time. DayOfWeek uses 0 = Sunday, matching <c>listing_occurrence.day_of_week</c>.</summary>
public sealed record Occurrence(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int DayOfWeek);

/// <summary>
/// The schedule a provider entered, as the local wall-clock rule it describes.
/// </summary>
public sealed record SessionRule(
    string TimeZoneId,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? RecurrenceRule,
    IReadOnlyList<DateOnly> ExcludedDates,
    bool IsAllDay = false);

/// <summary>
/// Expands a session's RFC 5545 rule into absolute occurrences, and does the
/// timezone arithmetic correctly across daylight-saving transitions.
/// </summary>
/// <remarks>
/// <para>
/// The recurrence is evaluated on <em>floating</em> local times, so
/// "every Friday at 9pm" stays at 9pm across the spring change. Each local
/// time is then converted to UTC with the tenant's timezone, which is the step
/// that goes wrong in naive implementations. A session written as 21:00 local
/// must not drift to 22:00 or 20:00 for half the year.
/// </para>
/// <para>
/// Two DST edge cases are handled explicitly. A local time that does not exist
/// (the hour skipped at spring-forward) moves forward one hour, which is what a
/// family will expect. A local time that occurs twice (the hour repeated at
/// fall-back) takes the earlier instant. Both are tested.
/// </para>
/// </remarks>
public static class OccurrenceExpander
{
    /// <summary>A hard ceiling so a malformed rule cannot materialise unbounded rows.</summary>
    public const int MaxOccurrencesPerExpansion = 1000;

    public static IReadOnlyList<Occurrence> Expand(SessionRule rule, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (to < from)
        {
            throw new ArgumentException("The end of the window must not be before its start.", nameof(to));
        }

        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId);
        DateTime startLocal = rule.StartsOn.ToDateTime(rule.StartTime);
        TimeSpan duration = rule.EndTime > rule.StartTime
            ? rule.EndTime.ToTimeSpan() - rule.StartTime.ToTimeSpan()
            : TimeSpan.Zero;

        List<DateTime> localStarts = LocalStarts(rule, startLocal, from, to);

        List<Occurrence> results = [];
        foreach (DateTime local in localStarts)
        {
            DateTimeOffset startsAt = ToUtc(local, zone);
            DateTimeOffset endsAt = ToUtc(local.Add(duration), zone);
            results.Add(new Occurrence(startsAt, endsAt, (int)local.DayOfWeek));

            if (results.Count >= MaxOccurrencesPerExpansion)
            {
                break;
            }
        }

        return results;
    }

    private static List<DateTime> LocalStarts(SessionRule rule, DateTime dtStart, DateOnly from, DateOnly to)
    {
        DateTime windowStart = from.ToDateTime(TimeOnly.MinValue);
        DateTime windowEnd = to.ToDateTime(TimeOnly.MaxValue);
        HashSet<DateOnly> excluded = [.. rule.ExcludedDates];

        if (string.IsNullOrWhiteSpace(rule.RecurrenceRule))
        {
            // A one-off: the single start, if it falls in the window.
            return dtStart >= windowStart && dtStart <= windowEnd && !excluded.Contains(rule.StartsOn)
                ? [dtStart]
                : [];
        }

        var calendarEvent = new CalendarEvent
        {
            Start = new CalDateTime(dtStart),
            End = new CalDateTime(dtStart.Add(TimeSpan.FromHours(1))),
        };

        calendarEvent.RecurrenceRule = ParseRule(rule.RecurrenceRule);

        // A rule with no UNTIL is unbounded, so enumerate lazily and stop at the
        // end of the window rather than asking the library for a finite set.
        List<DateTime> starts = [];
        foreach (var occurrence in calendarEvent.GetOccurrences(new CalDateTime(windowStart)))
        {
            DateTime local = occurrence.Period.StartTime.Value;

            if (local > windowEnd)
            {
                break;
            }

            if (local < windowStart)
            {
                continue;
            }

            // The session's end date bounds the rule, whatever UNTIL the provider
            // wrote, so the two cannot disagree.
            if (rule.EndsOn is { } last && DateOnly.FromDateTime(local) > last)
            {
                continue;
            }

            if (excluded.Contains(DateOnly.FromDateTime(local)))
            {
                continue;
            }

            starts.Add(local);
        }

        return starts;
    }

    private static RecurrencePattern ParseRule(string rule)
    {
        string normalised = rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)
            ? rule["RRULE:".Length..]
            : rule;

        return new RecurrencePattern(normalised);
    }

    /// <summary>
    /// Converts a wall-clock time in a zone to UTC, handling the two DST edge
    /// cases documented on the class.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            // The hour does not exist. Move forward past the gap.
            unspecified = unspecified.AddHours(1);
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            // The hour occurs twice. The earlier instant is the one that is
            // still daylight time, i.e. the larger offset.
            TimeSpan offset = zone.GetAmbiguousTimeOffsets(unspecified).Max();
            return new DateTimeOffset(unspecified, offset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }
}
