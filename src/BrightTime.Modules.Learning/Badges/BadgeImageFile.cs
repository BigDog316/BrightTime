using System.Buffers.Binary;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Learning.Badges;

/// <summary>An image checked and cleaned for a badge: its type, its bytes, and its size in pixels.</summary>
public sealed record CleanImage(string ContentType, byte[] Bytes, int Width, int Height);

/// <summary>
/// A badge's own image (D87), read without an imaging package: a PNG or a baseline or progressive
/// JPEG, from 64 to 2,048 pixels on each side, in up to 512 KB. Everything that isn't the picture
/// is dropped: PNG text, time and EXIF chunks and animation; JPEG EXIF, XMP and other application
/// segments, and comments. Nothing typed into a camera or an editor reaches the badge.
/// </summary>
/// <remarks>
/// The picture itself is copied as it came: this is not a decoder, so a file that claims to be a
/// PNG but holds nonsense is stored as nonsense, and a browser shows nothing. The image is served
/// with its own type and <c>nosniff</c>, so it is never read as anything else.
/// </remarks>
public static class BadgeImageFile
{
    public const int MaxBytes = 512 * 1024;
    public const int MinSide = 64;
    public const int MaxSide = 2048;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The PNG chunks kept: the picture, and how its colours are meant to look.</summary>
    private static readonly HashSet<string> PngKept = new(StringComparer.Ordinal) { "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB" };

    public static Result<CleanImage> Clean(ReadOnlySpan<byte> file)
    {
        CleanImage? clean = file.Length is 0 or > MaxBytes ? null
            : file.StartsWith(PngSignature) ? Png(file)
            : file.Length > 4 && file[0] == 0xFF && file[1] == 0xD8 ? Jpeg(file)
            : null;
        return clean is { Width: >= MinSide and <= MaxSide, Height: >= MinSide and <= MaxSide }
            ? Result<CleanImage>.Success(clean)
            : Result<CleanImage>.Failure(Error.Validation("badges.image", "Choose a PNG or JPEG of up to 512 KB, from 64 to 2,048 pixels on each side."));
    }

    private static CleanImage? Png(ReadOnlySpan<byte> file)
    {
        using MemoryStream kept = new();
        kept.Write(PngSignature);
        int at = PngSignature.Length;
        int width = 0, height = 0;
        bool header = false, ended = false;
        while (at + 12 <= file.Length && !ended)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(file[at..]);
            if (length > (uint)(file.Length - at - 12))
            {
                return null;
            }

            string type = System.Text.Encoding.ASCII.GetString(file.Slice(at + 4, 4));
            ReadOnlySpan<byte> chunk = file.Slice(at, (int)length + 12);
            if (!header)
            {
                if (type != "IHDR" || length < 8)
                {
                    return null;
                }

                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(file[(at + 8)..]), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(file[(at + 12)..]), int.MaxValue);
                header = true;
            }

            if (PngKept.Contains(type))
            {
                kept.Write(chunk);
            }

            ended = type == "IEND";
            at += chunk.Length;
        }

        return header && ended ? new CleanImage("image/png", kept.ToArray(), width, height) : null;
    }

    private static CleanImage? Jpeg(ReadOnlySpan<byte> file)
    {
        using MemoryStream kept = new();
        kept.Write([0xFF, 0xD8]);
        int at = 2;
        int width = 0, height = 0;
        while (at + 4 <= file.Length)
        {
            if (file[at] != 0xFF)
            {
                return null;
            }

            byte marker = file[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 2)..]);
            if (length < 2 || at + 2 + length > file.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> segment = file.Slice(at, length + 2);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && length >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 7)..]);
            }

            if (marker == 0xDA)
            {
                // The scan and everything after it is the picture, to the end of the file.
                kept.Write(file[at..]);
                return width > 0 ? new CleanImage("image/jpeg", kept.ToArray(), width, height) : null;
            }

            // Tables, frame headers and restart intervals stay; so do JFIF and Adobe's colour marker.
            // Every other application segment (EXIF, XMP, ICC and the rest) and comments go.
            bool keep = marker is 0xDB or 0xC4 or 0xDD or 0xE0 or 0xEE || (marker is >= 0xC0 and <= 0xCF);
            if (keep)
            {
                kept.Write(segment);
            }

            at += segment.Length;
        }

        return null;
    }
}
