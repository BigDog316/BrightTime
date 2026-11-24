using System.Buffers.Binary;

namespace BrightTime.SharedKernel.Media;

/// <summary>An image checked and cleaned: its type, its bytes, and its size in pixels as it's shown.</summary>
public sealed record CleanImage(string ContentType, byte[] Bytes, int Width, int Height);

/// <summary>
/// Reads a PNG or a baseline or progressive JPEG without an imaging package (D87, D103), and drops
/// everything that isn't the picture: PNG text, time and EXIF chunks and animation; JPEG EXIF, XMP
/// and other application segments, and comments. Nothing typed into a camera or an editor, and no
/// place a photo was taken, survives. A JPEG's orientation is the one exception: it's written back
/// alone, so a phone photo isn't shown on its side.
/// </summary>
/// <remarks>
/// The picture itself is copied as it came: this is not a decoder, so a file that claims to be a
/// PNG but holds nonsense is stored as nonsense, and a browser shows nothing. It's served with its
/// own type and <c>nosniff</c>, so it's never read as anything else. Anything else, SVG included,
/// isn't an image here.
/// </remarks>
public static class ImageCleaner
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The PNG chunks kept: the picture, and how its colours are meant to look.</summary>
    private static readonly HashSet<string> PngKept = new(StringComparer.Ordinal) { "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB" };

    /// <summary>The cleaned image, or null when the file is empty, larger than <paramref name="maxBytes"/>, or not a whole PNG or JPEG.</summary>
    public static CleanImage? Clean(ReadOnlySpan<byte> file, int maxBytes) =>
        file.Length == 0 || file.Length > maxBytes ? null
        : file.StartsWith(PngSignature) ? Png(file)
        : file.Length > 4 && file[0] == 0xFF && file[1] == 0xD8 ? Jpeg(file)
        : null;

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
        int at = 2;
        int width = 0, height = 0;
        ushort orientation = 1;
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

            if (marker == 0xE1 && orientation == 1)
            {
                orientation = Orientation(segment[4..]);
            }

            if (marker == 0xDA)
            {
                // The scan and everything after it is the picture, to the end of the file.
                kept.Write(file[at..]);
                if (width == 0)
                {
                    return null;
                }

                // Turned a quarter either way (5 to 8), it's shown with its sides swapped.
                bool turned = orientation >= 5;
                return new CleanImage("image/jpeg", [0xFF, 0xD8, .. OrientationSegment(orientation), .. kept.ToArray()],
                    turned ? height : width, turned ? width : height);
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

    /// <summary>The EXIF orientation (1 to 8) in an APP1 segment's data, or 1 when there's none.</summary>
    private static ushort Orientation(ReadOnlySpan<byte> app1)
    {
        if (app1.Length < 14 || !app1.StartsWith("Exif\0\0"u8))
        {
            return 1;
        }

        ReadOnlySpan<byte> tiff = app1[6..];
        bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M'))
        {
            return 1;
        }

        uint ifd = U32(tiff[4..], little);
        if (ifd < 8 || ifd > (uint)(tiff.Length - 2))
        {
            return 1;
        }

        int count = U16(tiff[(int)ifd..], little);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            // Tag 0x0112, a SHORT: the value sits in the first two bytes of the value field.
            if (U16(tiff[entry..], little) == 0x0112 && U16(tiff[(entry + 2)..], little) == 3)
            {
                ushort value = U16(tiff[(entry + 8)..], little);
                return value is >= 1 and <= 8 ? value : (ushort)1;
            }
        }

        return 1;
    }

    /// <summary>An APP1 segment holding nothing but the orientation, or nothing when it's upright.</summary>
    private static byte[] OrientationSegment(ushort orientation) => orientation == 1 ? [] :
    [
        0xFF, 0xE1, 0x00, 0x22,
        (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
        (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x01,
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private static ushort U16(ReadOnlySpan<byte> at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at);

    private static uint U32(ReadOnlySpan<byte> at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at);
}
