namespace BrightTime.Abstractions.Storage;

public enum ScanVerdict
{
    Pending,
    Clean,
    Infected,
    Failed,
}

public sealed record ScanResult(ScanVerdict Verdict, string? Detail);

/// <summary>Scans an uploaded object. Nothing is servable until the verdict is <see cref="ScanVerdict.Clean"/>.</summary>
public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(string objectKey, CancellationToken cancellationToken = default);
}
