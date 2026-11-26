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

    /// <summary>When a public image's smaller copies were made (D106); null until then.</summary>
    public DateTimeOffset? SizedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A row of <c>platform.media_variant</c>: a smaller WebP copy of a public image (D106).</summary>
public sealed class MediaVariant
{
    public Guid TenantId { get; init; }

    public Guid MediaId { get; init; }

    /// <summary>The size asked for, one of <c>MediaSizes.All</c>: the copy is this wide.</summary>
    public int Size { get; init; }

    public required string ObjectKey { get; init; }

    public required string ContentType { get; init; }

    public long ByteSize { get; init; }

    public required byte[] Sha256 { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

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
