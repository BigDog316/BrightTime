using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Learning.Badges;

/// <summary>
/// A badge's own image (D87): a PNG or JPEG from 64 to 2,048 pixels on each side, in up to 512 KB.
/// Since D112 it's a public media object (D103), cleaned and checked like any picture, with its
/// smaller copies (D106).
/// </summary>
public static class BadgeImageRules
{
    public const int MaxBytes = 512 * 1024;
    public const int MinSide = 64;
    public const int MaxSide = 2048;

    public static ImageRules Image { get; } = new(MaxBytes, MinSide, MinSide, MaxSide);

    public static Error NotAnImage => Error.Validation("badges.image", "Choose a PNG or JPEG of up to 512 KB, from 64 to 2,048 pixels on each side.");
}
