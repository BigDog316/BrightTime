namespace BrightTime.Modules.Platform.Domain;

/// <summary>A row of <c>platform.media_object</c>: a media object's metadata and its safety gate.</summary>
public sealed class MediaObject
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string ObjectKey { get; init; }

    public required string ContentType { get; init; }

    public long ByteSize { get; init; }

    public required byte[] Sha256 { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>For sound and video; null for an image.</summary>
    public int? DurationMs { get; init; }

    public string Origin { get; init; } = "upload";

    public string ScanState { get; set; } = "pending";

    public string? ScanDetail { get; set; }

    public DateTimeOffset? ExifStrippedAt { get; init; }

    public Guid? UploadedBy { get; init; }

    public string Visibility { get; set; } = "private";

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A row of <c>platform.media_blob</c>: the bytes behind an object key (D103).</summary>
public sealed class MediaBlob
{
    public Guid TenantId { get; init; }

    public required string ObjectKey { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Bytes { get; init; }

    public required byte[] Sha256 { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
