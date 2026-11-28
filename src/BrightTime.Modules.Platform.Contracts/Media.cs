using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Platform.Contracts;

/// <summary>Who may see a media object, as <c>ck_media_visibility</c>.</summary>
public static class MediaVisibility
{
    public const string Private = "private";
    public const string Organisation = "org";
    public const string Tenant = "tenant";
    public const string Public = "public";
}

/// <summary>
/// What a feature accepts as an image: its file size, and its size in pixels as it's shown. A
/// <paramref name="MaxAspect"/> above zero is how many times longer one side may be than the
/// other (D109).
/// </summary>
public sealed record ImageRules(int MaxBytes, int MinWidth, int MinHeight, int MaxSide, double MaxAspect = 0);

/// <summary>An image kept as a media object.</summary>
public sealed record StoredImage(Guid MediaId, int Width, int Height);

/// <summary>
/// Why the library didn't keep a file (D123). <see cref="NotAPicture"/> is for the feature to say
/// in its own words (<see cref="Or"/>); the others are said the same everywhere.
/// </summary>
public static class MediaRefusals
{
    /// <summary>Not a whole PNG or JPEG that meets the feature's rules and decodes.</summary>
    public static readonly Error NotAPicture = Error.Validation("media.not_a_picture", "The file isn't a picture this accepts.");

    /// <summary>The virus scan found something: the file isn't kept, and the refusal is audited.</summary>
    public static readonly Error Infected = Error.Validation(
        "media.infected", "The virus check found something in this file, so it wasn't kept. Try another copy of the picture.");

    /// <summary>The virus scan couldn't be done: the file isn't kept unchecked.</summary>
    public static readonly Error Unchecked = new(
        ErrorKind.Unavailable, "media.unchecked", "The picture couldn't be checked just now, so it wasn't kept. Try again in a few minutes.");

    /// <summary><paramref name="refusal"/>, with <see cref="NotAPicture"/> said as the feature says it.</summary>
    public static Error Or(Error refusal, Error notAPicture)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal.Code == NotAPicture.Code ? notAPicture : refusal;
    }
}

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
    /// Keeps a PNG or JPEG that meets <paramref name="rules"/>, has passed the virus scan and
    /// decodes whole, made again from its pixels so nothing else of the upload is kept (D123), and a
    /// public one's smaller copies (D106). A failure is one of <see cref="MediaRefusals"/>: the
    /// caller says <see cref="MediaRefusals.NotAPicture"/> in its own words.
    /// </summary>
    Task<Result<StoredImage>> AddImageAsync(
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
