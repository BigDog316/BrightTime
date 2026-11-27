using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BrightTime.Api.Tests;

/// <summary>
/// Pictures for a badge's own image (D87): a decodable PNG, with a text chunk when asked. Since
/// D112 the media library checks and cleans them (ImageCleanerTests and MediaSizesTests cover
/// that); BadgeApiTests uploads these.
/// </summary>
internal static class BadgeTestImages
{
    /// <summary>A real, decodable PNG: a grey square, with a text chunk a camera or editor might add.</summary>
    internal static byte[] Png(int side, string? text = null)
    {
        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)side);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)side);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        Chunk(png, "IHDR", header);
        if (text is not null)
        {
            Chunk(png, "tEXt", Encoding.Latin1.GetBytes("Comment\0" + text));
        }

        using MemoryStream raw = new();
        for (int y = 0; y < side; y++)
        {
            raw.WriteByte(0); // no filter
            raw.Write(Enumerable.Repeat((byte)0x80, side).ToArray());
        }

        using MemoryStream packed = new();
        using (ZLibStream z = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }

        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
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
