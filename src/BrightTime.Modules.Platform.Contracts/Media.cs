using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Platform.Contracts;

/// <summary>Who may see a media object, as <c>ck_media_visibility</c>.</summary>
public static class MediaVisibility
{
    public const string Private = "private";
    public const string Organisation = "org";
    public const string Tenant = "tenant";
    public const string Public = "public";
}

/// <summary>What a feature accepts as an image: its file size, and its size in pixels as it's shown.</summary>
public sealed record ImageRules(int MaxBytes, int MinWidth, int MinHeight, int MaxSide);

/// <summary>An image kept as a media object.</summary>
public sealed record StoredImage(Guid MediaId, int Width, int Height);

/// <summary>A media object's bytes, ready to send, with a tag that changes only with the bytes.</summary>
public sealed record MediaFile(string ContentType, byte[] Bytes, string ETag);

/// <summary>
/// The tenant's media (D103): images checked and cleaned on the way in, kept with their
/// metadata, and handed out only when they're clean and visible to whoever asks.
/// </summary>
public interface IMediaLibrary
{
    /// <summary>
    /// Keeps a PNG or JPEG that meets <paramref name="rules"/>, with its metadata stripped. Null
    /// when the file isn't one: the caller says why, in its own words.
    /// </summary>
    Task<StoredImage?> AddImageAsync(
        ReadOnlyMemory<byte> file, ImageRules rules, string visibility, UserId uploadedBy, CancellationToken cancellationToken = default);

    /// <summary>A clean, public media object's bytes; null for anything else, so an id alone gives nothing away.</summary>
    Task<MediaFile?> GetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>Removes a media object and its bytes. Whatever pointed to it must have let go first.</summary>
    Task<bool> RemoveAsync(Guid mediaId, UserId actor, CancellationToken cancellationToken = default);
}
