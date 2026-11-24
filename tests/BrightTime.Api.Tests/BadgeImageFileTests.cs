using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BrightTime.Modules.Learning.Badges;
using BrightTime.SharedKernel.Media;

namespace BrightTime.Api.Tests;

/// <summary>
/// A badge's own image (D87): a PNG or JPEG of the right size is kept, with everything that isn't
/// the picture dropped; anything else is refused.
/// </summary>
public sealed class BadgeImageFileTests
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

    /// <summary>A JPEG's segments around a frame header of the given size: JFIF, EXIF with a place in it, a comment, the tables and a scan.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using MemoryStream jpeg = new();
        jpeg.Write([0xFF, 0xD8]);
        Segment(jpeg, 0xE0, [.. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(jpeg, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. "GPS 40.7128 N 74.0060 W"u8.ToArray()]);
        Segment(jpeg, 0xFE, "Taken at home"u8.ToArray());
        Segment(jpeg, 0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        byte[] frame = [8, 0, 0, 0, 0, 1, 1, 0x11, 0];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)width);
        Segment(jpeg, 0xC0, frame);
        Segment(jpeg, 0xDA, [1, 1, 0, 0, 63, 0]);
        jpeg.Write([0x12, 0x34, 0x56, 0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static void Segment(Stream jpeg, byte marker, byte[] data)
    {
        jpeg.Write([0xFF, marker]);
        byte[] length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(data.Length + 2));
        jpeg.Write(length);
        jpeg.Write(data);
    }

    [Fact]
    public void A_png_keeps_its_picture_and_loses_its_text()
    {
        byte[] file = Png(96, "Made at 12 Elm Street");
        CleanImage clean = BadgeImageFile.Clean(file).Value;
        (clean.ContentType, clean.Width, clean.Height).ShouldBe(("image/png", 96, 96));
        Encoding.Latin1.GetString(clean.Bytes).ShouldNotContain("Elm Street");
        clean.Bytes.ShouldBe(Png(96), "the same picture, chunk for chunk, without the text");
    }

    [Fact]
    public void A_jpeg_keeps_its_frame_and_scan_and_loses_its_exif_and_comments()
    {
        CleanImage clean = BadgeImageFile.Clean(Jpeg(300, 200)).Value;
        (clean.ContentType, clean.Width, clean.Height).ShouldBe(("image/jpeg", 300, 200));
        string kept = Encoding.Latin1.GetString(clean.Bytes);
        kept.ShouldContain("JFIF");
        kept.ShouldNotContain("GPS");
        kept.ShouldNotContain("Taken at home");
        clean.Bytes[^2..].ShouldBe(new byte[] { 0xFF, 0xD9 });
    }

    [Theory]
    [InlineData(32)]
    [InlineData(4096)]
    public void A_picture_too_small_or_too_large_is_refused(int side) =>
        BadgeImageFile.Clean(side == 32 ? Png(32) : Jpeg(side, side)).Error!.Code.ShouldBe("badges.image");

    [Fact]
    public void Anything_else_is_refused()
    {
        BadgeImageFile.Clean("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8).IsSuccess.ShouldBeFalse();
        BadgeImageFile.Clean("GIF89a"u8).IsSuccess.ShouldBeFalse();
        BadgeImageFile.Clean([]).IsSuccess.ShouldBeFalse();
        BadgeImageFile.Clean(Png(96).AsSpan(0, 40)).IsSuccess.ShouldBeFalse("a PNG cut short has no end");
        BadgeImageFile.Clean(new byte[BadgeImageFile.MaxBytes + 1]).IsSuccess.ShouldBeFalse();
    }
}
