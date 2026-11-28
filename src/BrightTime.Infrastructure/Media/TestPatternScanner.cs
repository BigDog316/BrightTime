using System.Text;
using BrightTime.Abstractions.Storage;

namespace BrightTime.Infrastructure.Media;

/// <summary>
/// Development's scanner when clamd isn't configured (D123): it finds the EICAR test file and
/// nothing else, so the refusal can be seen and tested without ClamAV. It isn't a virus scan, and
/// <c>scan_detail</c> says so. Outside Development a host without clamd doesn't start.
/// </summary>
public sealed class TestPatternScanner : IVirusScanner
{
    public const string Engine = "test pattern only (Development): not virus-scanned";

    /// <summary>The name it gives what it finds, as ClamAV names the same file.</summary>
    public const string Signature = "Eicar-Test-Signature";

    /// <summary>
    /// The EICAR anti-virus test file: harmless, and found by every scanner. Put together when it's
    /// first asked for, so no source or binary file here holds it whole for a scanner on a
    /// developer's machine to quarantine.
    /// </summary>
    public static ReadOnlyMemory<byte> TestFile { get; } =
        Encoding.ASCII.GetBytes(string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-TEST-", "FILE!$H+H*"));

    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        Task.FromResult(content.Span.IndexOf(TestFile.Span) >= 0
            ? new ScanResult(ScanVerdict.Infected, Engine, Signature)
            : new ScanResult(ScanVerdict.Clean, Engine));
}
