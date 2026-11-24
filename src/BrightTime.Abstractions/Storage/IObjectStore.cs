namespace BrightTime.Abstractions.Storage;

public sealed record StoredObject(string ObjectKey, string ContentType, long ByteSize, byte[] Sha256);

/// <summary>
/// Where media bytes live, by object key, under the current tenant. D103 keeps them in the
/// database (<c>platform.media_blob</c>); an S3-compatible store can take its place without the
/// modules noticing. Nothing is served from the store directly: the application checks
/// <c>media_object.scan_state = 'clean'</c>, the visibility and the caller first, then streams the
/// bytes itself, so there are no presigned URLs.
/// </summary>
/// <remarks>
/// The database store writes in the caller's transaction on the Platform context, so the bytes
/// and their <c>media_object</c> row are kept or lost together. Another store couldn't promise
/// that, and would need a sweep for bytes whose row never committed.
/// </remarks>
public interface IObjectStore
{
    Task<StoredObject> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>The bytes, or null when nothing is stored under the key.</summary>
    Task<byte[]?> ReadAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}
