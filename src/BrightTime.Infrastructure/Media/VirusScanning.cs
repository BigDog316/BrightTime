using BrightTime.Abstractions.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BrightTime.Infrastructure.Media;

public static class VirusScanning
{
    /// <summary>
    /// The upload scanner (D123): clamd when <c>Media:Clamd:Host</c> is set; otherwise, in
    /// Development only, <see cref="TestPatternScanner"/>. A host outside Development without clamd
    /// doesn't start, so nothing is ever kept unscanned there; a container with no host at all (a
    /// test's) gets a scanner that can't check anything. A second call changes nothing.
    /// </summary>
    public static IServiceCollection AddVirusScanning(this IServiceCollection services)
    {
        services.TryAddSingleton<IVirusScanner>(sp =>
        {
            ClamdOptions clamd = sp.GetService<IConfiguration>()?.GetSection(ClamdOptions.SectionName).Get<ClamdOptions>() ?? new ClamdOptions();
            if (clamd.IsConfigured)
            {
                return new ClamdScanner(Options.Create(clamd), sp.GetService<ILogger<ClamdScanner>>() ?? NullLogger<ClamdScanner>.Instance);
            }

            return sp.GetService<IHostEnvironment>() switch
            {
                null => new NoScanner(),
                { } environment when environment.IsDevelopment() => new TestPatternScanner(),
                _ => throw new InvalidOperationException(
                    "Media:Clamd:Host is required outside Development: uploads are virus-scanned before they're kept (D123)."),
            };
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, VirusScannerCheck>());
        return services;
    }
}

/// <summary>Where nothing is configured and there's no host to say otherwise: every file is unchecked, so none is kept.</summary>
internal sealed class NoScanner : IVirusScanner
{
    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScanResult(ScanVerdict.Failed, "none", "not configured"));
}

/// <summary>Resolves the scanner as the host starts, so a missing clamd setting stops it there rather than at the first upload.</summary>
internal sealed class VirusScannerCheck(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<IVirusScanner>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
