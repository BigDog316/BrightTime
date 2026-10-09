namespace BrightTime.Abstractions.Storage;

public sealed record SanitisedMedia(
    Stream Content,
    string ContentType,
    int? Width,
    int? Height,
    bool MetadataStripped);

/// <summary>
/// Strips metadata and re-encodes uploaded images.
/// </summary>
/// <remarks>
/// Two jobs, both load-bearing. A photo taken on a phone carries GPS
/// coordinates in EXIF — publishing a youth's untouched camera upload
/// publishes where they were, which is why
/// <c>ck_media_exif_before_public</c> refuses to make camera-origin media
/// public until <c>exif_stripped_at</c> is set. Re-encoding also destroys
/// payloads smuggled in image containers. SVG is rejected outright: it is a
/// script container wearing an image's extension.
/// </remarks>
public interface IMediaSanitiser
{
    bool IsAllowedContentType(string contentType);

    Task<SanitisedMedia> SanitiseAsync(Stream input, string declaredContentType, CancellationToken cancellationToken = default);
}
