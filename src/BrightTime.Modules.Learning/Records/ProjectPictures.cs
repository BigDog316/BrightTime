using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Modules.Learning.Records;

/// <summary>A picture on a project (D104): the media object, what it shows, and a caption if there is one.</summary>
[YouthData]
public sealed record ProjectPicture(Guid MediaId, string Alt, string? Caption);

/// <summary>
/// What a project's pictures are held to (D104): up to six PNG or JPEG photos of up to 5 MB, at
/// least 320 pixels on each side, each saying what it shows.
/// </summary>
public static class ProjectPictures
{
    public const int MaxPictures = 6;
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MinSide = 320;
    public const int MaxSide = 8000;
    public const int MaxAlt = 250;
    public const int MaxCaption = 300;

    public static readonly ImageRules Rules = new(MaxBytes, MinSide, MinSide, MaxSide);

    public static Error NotAPicture { get; } =
        Error.Validation("projects.picture", "Choose a PNG or JPEG photo of up to 5 MB, at least 320 pixels on each side.");

    public static Error Full { get; } =
        Error.Conflict("projects.pictures_full", $"A project has up to {MaxPictures} pictures.");

    public static Error NotFound { get; } =
        Error.NotFound("projects.picture_not_found", "That project has no picture like that.");

    /// <summary>What the picture shows and its caption, trimmed, or the reason they won't do.</summary>
    public static Result<(string Alt, string? Caption)> Words(string? alt, string? caption)
    {
        string said = (alt ?? string.Empty).Trim();
        if (said.Length is 0 or > MaxAlt)
        {
            return Result<(string, string?)>.Failure(Error.Validation("projects.picture_alt", $"Say what the picture shows, in up to {MaxAlt} characters."));
        }

        string? written = caption?.Trim() is { Length: > 0 } c ? c : null;
        return written is { Length: > MaxCaption }
            ? Result<(string, string?)>.Failure(Error.Validation("projects.picture_caption", $"A caption of up to {MaxCaption} characters."))
            : Result<(string, string?)>.Success((said, written));
    }
}
