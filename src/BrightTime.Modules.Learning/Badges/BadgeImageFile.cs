using BrightTime.SharedKernel.Media;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Learning.Badges;

/// <summary>
/// A badge's own image (D87): a PNG or JPEG from 64 to 2,048 pixels on each side, in up to 512 KB,
/// cleaned by <see cref="ImageCleaner"/> so nothing but the picture reaches the badge.
/// </summary>
public static class BadgeImageFile
{
    public const int MaxBytes = 512 * 1024;
    public const int MinSide = 64;
    public const int MaxSide = 2048;

    public static Result<CleanImage> Clean(ReadOnlySpan<byte> file) =>
        ImageCleaner.Clean(file, MaxBytes) is { Width: >= MinSide and <= MaxSide, Height: >= MinSide and <= MaxSide } clean
            ? Result<CleanImage>.Success(clean)
            : Result<CleanImage>.Failure(Error.Validation("badges.image", "Choose a PNG or JPEG of up to 512 KB, from 64 to 2,048 pixels on each side."));
}
