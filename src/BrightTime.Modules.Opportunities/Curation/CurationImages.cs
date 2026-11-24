using System.Text.Json;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Opportunities.Curation;

/// <summary>
/// A collection's or neighbourhood page's hero image (D103): a wide photo, shown across the top of
/// the page and cropped to fit, with what it shows said for anyone who can't see it.
/// </summary>
public static class CurationImages
{
    public const int MaxBytes = 3 * 1024 * 1024;
    public const int MinWidth = 960;
    public const int MinHeight = 320;
    public const int MaxSide = 6000;
    public const int MaxAlt = 250;

    public static readonly ImageRules Hero = new(MaxBytes, MinWidth, MinHeight, MaxSide);

    public static Error NotAnImage { get; } =
        Error.Validation("curation.hero_image", "Choose a PNG or JPEG photo of up to 3 MB, at least 960 by 320 pixels.");

    /// <summary>The description, trimmed, or the reason it won't do.</summary>
    public static Result<string> Alt(string? text)
    {
        string alt = (text ?? string.Empty).Trim();
        return alt.Length is > 0 and <= MaxAlt
            ? Result<string>.Success(alt)
            : Result<string>.Failure(Error.Validation("curation.hero_alt", $"Say what the picture shows, in up to {MaxAlt} characters."));
    }

    /// <summary>A neighbourhood page's descriptions by locale, from their JSON column.</summary>
    public static Dictionary<string, string> Alts(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? [];

    public static string Json(IReadOnlyDictionary<string, string> alts) => JsonSerializer.Serialize(alts);
}
