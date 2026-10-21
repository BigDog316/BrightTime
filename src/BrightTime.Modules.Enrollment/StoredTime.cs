using BrightTime.SharedKernel.Time;

namespace BrightTime.Modules.Enrollment;

internal static class StoredTime
{
    /// <summary>
    /// Now, to the microsecond PostgreSQL keeps, so a time returned when a row is written equals
    /// the time read back from it later.
    /// </summary>
    public static DateTimeOffset StoredNow(this IClock clock)
    {
        DateTimeOffset now = clock.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
