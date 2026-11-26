using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace BrightTime.SharedKernel.Tests;

/// <summary>
/// Small images for tests (D103): a decodable grey PNG of any size, built by hand, with an optional
/// text chunk; and a real grey JPEG, encoded by Skia (D106), with EXIF holding a place and, if
/// asked, an orientation, and a comment. Linked into the web, API and platform tests, which upload
/// them.
/// </summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height, string? text = null)
    {
        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        Chunk(png, "IHDR", header);
        if (text is not null)
        {
            Chunk(png, "tEXt", Encoding.Latin1.GetBytes("Comment\0" + text));
        }

        using MemoryStream packed = new();
        using (ZLibStream z = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = [0, .. Enumerable.Repeat((byte)0x80, width)];
            for (int y = 0; y < height; y++)
            {
                z.Write(row);
            }
        }

        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// A JPEG of <paramref name="width"/> by <paramref name="height"/> as stored, whose EXIF holds a
    /// place and, when <paramref name="orientation"/> isn't 1, that orientation; little-endian if
    /// asked. Grey, with the stored top-left quarter black when <paramref name="marked"/>, so a
    /// test can tell which way it's turned.
    /// </summary>
    public static byte[] Jpeg(int width, int height, ushort orientation = 1, bool littleEndian = false, bool marked = false)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(new SKColor(0x80, 0x80, 0x80));
            if (marked)
            {
                using SKPaint black = new() { Color = SKColors.Black };
                canvas.DrawRect(0, 0, width / 2f, height / 2f, black);
            }
        }

        using SKPixmap pixels = bitmap.PeekPixels();
        using SKData encoded = pixels.Encode(new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
            ?? throw new InvalidOperationException("Skia couldn't encode a JPEG.");
        byte[] skia = encoded.ToArray();

        // Skia's own segments, less its application ones, then the scan as it came.
        using MemoryStream jpeg = new();
        jpeg.Write([0xFF, 0xD8]);
        Segment(jpeg, 0xE1, Exif(orientation, littleEndian));
        Segment(jpeg, 0xE0, [.. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(jpeg, 0xFE, "Taken at home"u8.ToArray());
        int at = 2;
        while (at + 4 <= skia.Length)
        {
            byte marker = skia[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(skia.AsSpan(at + 2));
            if (marker == 0xDA)
            {
                jpeg.Write(skia.AsSpan(at));
                break;
            }

            if (marker is not (>= 0xE0 and <= 0xEF) and not 0xFE)
            {
                jpeg.Write(skia.AsSpan(at, length + 2));
            }

            at += length + 2;
        }

        return jpeg.ToArray();
    }

    /// <summary>EXIF with a make that names a place, then the orientation if any: two entries in IFD0.</summary>
    private static byte[] Exif(ushort orientation, bool little)
    {
        byte[] place = "GPS 40.7128 N 74.0060 W\0"u8.ToArray();
        int entries = orientation == 1 ? 1 : 2;
        int ifdSize = 2 + (entries * 12) + 4;
        byte[] tiff = new byte[8 + ifdSize + place.Length];
        void U16(int at, ushort v)
        {
            if (little) { BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), v); } else { BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), v); }
        }

        void U32(int at, uint v)
        {
            if (little) { BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), v); } else { BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), v); }
        }

        tiff[0] = tiff[1] = (byte)(little ? 'I' : 'M');
        U16(2, 42);
        U32(4, 8);
        U16(8, (ushort)entries);
        // 0x010F Make, ASCII, pointing past the IFD.
        U16(10, 0x010F);
        U16(12, 2);
        U32(14, (uint)place.Length);
        U32(18, (uint)(8 + ifdSize));
        if (orientation != 1)
        {
            U16(22, 0x0112);
            U16(24, 3);
            U32(26, 1);
            U16(30, orientation);
        }

        place.CopyTo(tiff, 8 + ifdSize);
        return [.. "Exif\0\0"u8.ToArray(), .. tiff];
    }

    private static void Segment(Stream jpeg, byte marker, byte[] data)
    {
        jpeg.Write([0xFF, marker]);
        byte[] length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(data.Length + 2));
        jpeg.Write(length);
        jpeg.Write(data);
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        png.Write(length);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        png.Write(typed);
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
