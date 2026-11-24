using System.Text;
using BrightTime.SharedKernel.Media;

namespace BrightTime.SharedKernel.Tests;

/// <summary>
/// Reading and cleaning an image without an imaging package (D87, D103): the picture is kept and
/// everything else goes, except a JPEG's orientation, which is written back alone so a phone photo
/// isn't shown on its side.
/// </summary>
public sealed class ImageCleanerTests
{
    private const int Max = 3 * 1024 * 1024;

    [Fact]
    public void A_png_keeps_its_picture_and_size_and_loses_its_text()
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Png(1200, 400, "Made at 12 Elm Street"), Max).ShouldNotBeNull();
        (clean.ContentType, clean.Width, clean.Height).ShouldBe(("image/png", 1200, 400));
        clean.Bytes.ShouldBe(TestImages.Png(1200, 400), "the same picture, chunk for chunk, without the text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_turned_jpeg_keeps_only_its_orientation_and_is_measured_as_shown(bool littleEndian)
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Jpeg(1600, 1200, orientation: 6, littleEndian), Max).ShouldNotBeNull();
        (clean.ContentType, clean.Width, clean.Height).ShouldBe(("image/jpeg", 1200, 1600), "turned a quarter, it's shown taller than wide");
        string kept = Encoding.Latin1.GetString(clean.Bytes);
        kept.ShouldNotContain("GPS");
        kept.ShouldNotContain("Taken at home");
        kept.ShouldContain("JFIF");
        clean.Bytes[..4].ShouldBe(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, "the orientation comes first, in a segment of its own");
        clean.Bytes[2..38].ShouldBe(new byte[]
        {
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        });
        clean.Bytes[^2..].ShouldBe(new byte[] { 0xFF, 0xD9 });
    }

    [Fact]
    public void An_upright_jpeg_keeps_no_exif_at_all()
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Jpeg(1600, 1200), Max).ShouldNotBeNull();
        (clean.Width, clean.Height).ShouldBe((1600, 1200));
        clean.Bytes.AsSpan().IndexOf("Exif"u8).ShouldBe(-1);
        clean.Bytes[..4].ShouldBe(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "straight to JFIF");
    }

    [Fact]
    public void Anything_else_is_not_an_image()
    {
        ImageCleaner.Clean("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8, Max).ShouldBeNull();
        ImageCleaner.Clean("GIF89a"u8, Max).ShouldBeNull();
        ImageCleaner.Clean([], Max).ShouldBeNull();
        ImageCleaner.Clean(TestImages.Png(96, 96).AsSpan(0, 40), Max).ShouldBeNull("a PNG cut short has no end");
        ImageCleaner.Clean(TestImages.Png(96, 96), 100).ShouldBeNull("larger than allowed");
    }
}
