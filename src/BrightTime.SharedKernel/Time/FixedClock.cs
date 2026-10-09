namespace BrightTime.SharedKernel.Time;

/// <summary>A settable <see cref="IClock"/> for tests and for replaying a backfill at a chosen instant.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    public DateOnly TodayIn(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, timeZone).DateTime);
    }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
