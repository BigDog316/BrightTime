namespace BrightTime.Abstractions.Storage;

public sealed record StoredObject(string ObjectKey, string ContentType, long ByteSize, byte[] Sha256);

/// <summary>
/// S3-compatible blob storage. Nothing is served directly from the bucket:
/// reads go through a short-lived presigned URL issued only after the
/// application has checked <c>media_object.scan_state = 'clean'</c> and the
/// caller's authorisation.
/// </summary>
public interface IObjectStore
{
    Task<StoredObject> PutAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>A time-limited read URL. Lifetime is deliberately short; youth media is never given a durable public URL.</summary>
    Task<Uri> GetPresignedReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken = default);

    Task<Uri> GetPresignedWriteUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
