namespace BrightTime.Abstractions.Storage;

/// <summary>What a scan found.</summary>
public enum ScanVerdict
{
    /// <summary>Nothing found.</summary>
    Clean,

    /// <summary>A signature matched: the file isn't kept.</summary>
    Infected,

    /// <summary>The file couldn't be checked (the scanner is down, timed out or refused it): it isn't kept either.</summary>
    Failed,
}

/// <summary>
/// A scan's verdict, the engine that gave it (for <c>scan_detail</c>), and what it found or what
/// went wrong: the signature's name when <see cref="ScanVerdict.Infected"/>, the reason when
/// <see cref="ScanVerdict.Failed"/>.
/// </summary>
public sealed record ScanResult(ScanVerdict Verdict, string Engine, string? Detail = null);

/// <summary>
/// Scans a file's bytes for malware before anything keeps them (D123). It fails closed: a scanner
/// that can't answer says <see cref="ScanVerdict.Failed"/>, never <see cref="ScanVerdict.Clean"/>.
/// </summary>
public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}
