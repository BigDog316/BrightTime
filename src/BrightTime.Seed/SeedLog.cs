using Microsoft.Extensions.Logging;

namespace BrightTime.Seed;

internal static partial class SeedLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Seed written for {Today}: {Added} new rows.")]
    public static partial void Written(ILogger logger, int added, DateOnly today);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Projected {Done} of {Total} listings.")]
    public static partial void Projected(ILogger logger, int done, int total);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Seed finished in {Seconds:0.0} s.")]
    public static partial void Done(ILogger logger, double seconds);
}
