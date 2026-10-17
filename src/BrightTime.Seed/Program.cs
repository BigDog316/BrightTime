using System.Diagnostics;
using System.Globalization;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.Logging;
using Npgsql;

// Seeds the fictional Kettle County tenant.
//
//   dotnet run --project src/BrightTime.Seed -- --connection "<connection string>" [--today 2026-10-16] [--skip-projection]
//
// The connection may also come from BRIGHTTIME_SEED_CONNECTION. It must bypass row-level
// security: a superuser locally, or a member of brighttime_migrator with
// "Options=-c role=brighttime_migrator". Safe to re-run: existing rows are left alone and
// every listing is re-projected.

using ILoggerFactory loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
ILogger logger = loggers.CreateLogger("seed");

string? connection = Arg("--connection") ?? Environment.GetEnvironmentVariable("BRIGHTTIME_SEED_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
{
    await Console.Error.WriteLineAsync("Usage: BrightTime.Seed --connection <connection string> [--today yyyy-MM-dd] [--skip-projection]");
    return 2;
}

TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(KettleListings.Zone);
DateOnly today = Arg("--today") is { } given
    ? DateOnly.ParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    : SystemClock.Instance.TodayIn(zone);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var clock = Stopwatch.StartNew();
SeedData data = KettleSeed.Build(today);

await using (NpgsqlDataSource source = NpgsqlDataSource.Create(connection))
{
    SeedCounts counts = await new SeedWriter(source).WriteAsync(data, cts.Token);
    SeedLog.Written(logger, counts.Added, today);
    foreach (string line in counts.Lines())
    {
        await Console.Out.WriteLineAsync(line);
    }
}

if (!args.Contains("--skip-projection"))
{
    await SeedProjection.ProjectAsync(connection, data.TenantId, [.. data.Listings.Select(l => l.Id)], logger, cts.Token);
}

SeedLog.Done(logger, clock.Elapsed.TotalSeconds);
return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
