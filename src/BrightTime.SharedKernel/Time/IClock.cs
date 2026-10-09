namespace BrightTime.SharedKernel.Time;

/// <summary>
/// The only source of "now". Nothing in the solution calls
/// <see cref="DateTimeOffset.UtcNow"/> directly; an architecture test enforces
/// it. Schedules, consent expiry, clearance expiry, retention windows and
/// badge validity are all time-dependent, and all of them need to be testable
/// at a chosen instant.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today { get; }

    /// <summary>
    /// Today's date in a tenant's own timezone. "Sessions this week" and
    /// "applications due today" are wall-clock questions in the region, not in UTC.
    /// </summary>
    DateOnly TodayIn(TimeZoneInfo timeZone);
}
