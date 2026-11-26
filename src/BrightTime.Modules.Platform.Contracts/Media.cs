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
/// The smaller copies made of a public image (D106), by width: one for cards and lists, one for a
/// page's main picture. A copy is WebP, and there's none of a size the image isn't wider than.
/// </summary>
public static class MediaSizes
{
    public const int Card = 480;

    public const int Page = 1280;

    public static IReadOnlyList<int> All { get; } = [Card, Page];

    public static bool IsSize(int width) => width is Card or Page;
}

/// <summary>
/// A public image at a size: the copy's bytes, or none, when the original is the one to show. Then
/// <paramref name="Settled"/> says whether that's for good (the copies were made and it isn't
/// wider) or only until the jobs worker makes them.
/// </summary>
public sealed record SizedMedia(MediaFile? File, bool Settled);

/// <summary>
/// The tenant's media (D103): images checked and cleaned on the way in, kept with their
/// metadata, and handed out only when they're clean and visible to whoever asks.
/// </summary>
public interface IMediaLibrary
{
    /// <summary>
    /// Keeps a PNG or JPEG that meets <paramref name="rules"/> and decodes whole, with its metadata
    /// stripped, and a public one's smaller copies (D106). Null when the file isn't one: the caller
    /// says why, in its own words.
    /// </summary>
    Task<StoredImage?> AddImageAsync(
        ReadOnlyMemory<byte> file, ImageRules rules, string visibility, UserId uploadedBy, CancellationToken cancellationToken = default);

    /// <summary>A clean, public media object's bytes; null for anything else, so an id alone gives nothing away.</summary>
    Task<MediaFile?> GetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>A clean, public image's copy at <paramref name="size"/> (one of <see cref="MediaSizes.All"/>); null when the image isn't one.</summary>
    Task<SizedMedia?> GetPublicSizeAsync(Guid mediaId, int size, CancellationToken cancellationToken = default);

    /// <summary>
    /// A clean media object's bytes, whatever its visibility, for a caller that has already decided
    /// who may see it (a young person's project pictures, D104); null for anything else.
    /// </summary>
    Task<MediaFile?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A clean image, whatever its visibility, as a JPEG for print (D107): its longest side at most
    /// <paramref name="longest"/> pixels, turned as it's shown. For a caller that has already
    /// decided who may see it; made when asked and never kept. Null for anything else, or an image
    /// that doesn't decode.
    /// </summary>
    Task<MediaFile?> GetPrintCopyAsync(Guid mediaId, int longest, CancellationToken cancellationToken = default);

    /// <summary>Removes a media object and its bytes. Whatever pointed to it must have let go first.</summary>
    Task<bool> RemoveAsync(Guid mediaId, UserId actor, CancellationToken cancellationToken = default);
}
