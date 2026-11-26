using System.Buffers.Binary;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Media;
using BrightTime.SharedKernel.Media;
using BrightTime.SharedKernel.Tests;
using SkiaSharp;

namespace BrightTime.Modules.Platform.Tests;

/// <summary>
/// The smaller copies of an image (D106): WebP, as wide as each size it's wider than, turned as
/// it's shown; and the decode that refuses a file that isn't a picture. And the copy for print
/// (D107): a JPEG no longer than asked on its longest side, turned, on white.
/// </summary>
public sealed class ImageSizerTests
{
    private const int Max = 10 * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IReadOnlyList<ImageCopy>?> CopiesAsync(CleanImage clean, IReadOnlyList<int> sizes) =>
        ImageSizer.CopiesAsync(clean.Bytes, clean.Width, clean.Height, sizes, Ct);

    private static SKBitmap Decode(byte[] webp)
    {
        webp[..4].ShouldBe("RIFF"u8.ToArray());
        webp[8..12].ShouldBe("WEBP"u8.ToArray());
        return SKBitmap.Decode(webp).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_photo_gets_a_copy_at_each_size_it_is_wider_than()
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Jpeg(1600, 900), Max).ShouldNotBeNull();
        IReadOnlyList<ImageCopy> copies = (await CopiesAsync(clean, MediaSizes.All)).ShouldNotBeNull();
        copies.Select(c => (c.Size, c.Width, c.Height)).ShouldBe([(480, 480, 270), (1280, 1280, 720)]);
        foreach (ImageCopy copy in copies)
        {
            using SKBitmap shown = Decode(copy.Bytes);
            (shown.Width, shown.Height).ShouldBe((copy.Width, copy.Height));
            copy.Bytes.Length.ShouldBeLessThan(clean.Bytes.Length);
        }
    }

    [Fact]
    public async Task A_png_is_copied_too_and_one_no_wider_than_a_size_gets_none_of_it()
    {
        CleanImage large = ImageCleaner.Clean(TestImages.Png(3000, 2000), Max).ShouldNotBeNull();
        (await CopiesAsync(large, MediaSizes.All)).ShouldNotBeNull().Select(c => (c.Size, c.Width, c.Height)).ShouldBe([(480, 480, 320), (1280, 1280, 853)]);

        CleanImage middling = ImageCleaner.Clean(TestImages.Png(1280, 720), Max).ShouldNotBeNull();
        (await CopiesAsync(middling, MediaSizes.All)).ShouldNotBeNull().Select(c => c.Size).ShouldBe([480], "1,280 wide is no wider than 1,280");

        CleanImage small = ImageCleaner.Clean(TestImages.Png(400, 300), Max).ShouldNotBeNull();
        (await CopiesAsync(small, MediaSizes.All)).ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public async Task Asking_for_no_sizes_only_decodes_it()
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Jpeg(1600, 1200, orientation: 6), Max).ShouldNotBeNull();
        (await CopiesAsync(clean, [])).ShouldNotBeNull().ShouldBeEmpty();
    }

    /// <summary>Where the stored top-left quarter is shown, for each EXIF orientation, and whether it's shown on its side.</summary>
    public static TheoryData<ushort, bool, bool, bool> Orientations => new()
    {
        { 1, false, false, false },
        { 2, false, true, false },
        { 3, true, true, false },
        { 4, true, false, false },
        { 5, false, false, true },
        { 6, false, true, true },
        { 7, true, true, true },
        { 8, true, false, true },
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task A_copy_is_turned_as_the_photo_is_shown(ushort orientation, bool bottom, bool right, bool turned)
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Jpeg(800, 600, orientation, marked: true), Max).ShouldNotBeNull();
        (clean.Width, clean.Height).ShouldBe(turned ? (600, 800) : (800, 600));
        ImageCopy copy = (await CopiesAsync(clean, [MediaSizes.Card])).ShouldNotBeNull().ShouldHaveSingleItem();
        (copy.Width, copy.Height).ShouldBe(turned ? (480, 640) : (480, 360));

        using SKBitmap shown = Decode(copy.Bytes);
        int x = right ? shown.Width * 3 / 4 : shown.Width / 4;
        int y = bottom ? shown.Height * 3 / 4 : shown.Height / 4;
        shown.GetPixel(x, y).Red.ShouldBeLessThan((byte)40, $"the black quarter is shown at ({x}, {y})");
        shown.GetPixel(shown.Width - 1 - x, shown.Height - 1 - y).Red.ShouldBeGreaterThan((byte)100, "and the opposite quarter is grey");
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_refused()
    {
        // A JPEG's headers around nonsense: the cleaner reads only the headers, and keeps it.
        byte[] fake = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 8, 0x02, 0x58, 0x03, 0x20, 1, 1, 0x11, 0, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0x34, 0x56, 0xFF, 0xD9];
        CleanImage looks = ImageCleaner.Clean(fake, Max).ShouldNotBeNull("the cleaner can't tell");
        (looks.Width, looks.Height).ShouldBe((800, 600));
        (await CopiesAsync(looks, MediaSizes.All)).ShouldBeNull();
        (await CopiesAsync(looks, [])).ShouldBeNull("not even to check it");

        // A PNG whose picture has been spoiled.
        byte[] png = TestImages.Png(800, 600);
        int data = png.AsSpan().IndexOf("IDAT"u8) + 4;
        png.AsSpan(data + 2, 40).Fill(0xA5);
        (await ImageSizer.CopiesAsync(png, 800, 600, MediaSizes.All, Ct)).ShouldBeNull();
    }

    private static SKBitmap DecodeJpeg(byte[] jpeg)
    {
        jpeg[..3].ShouldBe(new byte[] { 0xFF, 0xD8, 0xFF });
        return SKBitmap.Decode(jpeg).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_print_copy_is_a_JPEG_no_longer_than_asked_and_turned_as_shown()
    {
        // Landscape, and a phone photo on its side: the longest side is held to 1,000.
        CleanImage wide = ImageCleaner.Clean(TestImages.Jpeg(4000, 3000), Max).ShouldNotBeNull();
        byte[] print = (await ImageSizer.PrintCopyAsync(wide.Bytes, wide.Width, wide.Height, 1000, Ct)).ShouldNotBeNull();
        using (SKBitmap shown = DecodeJpeg(print))
        {
            (shown.Width, shown.Height).ShouldBe((1000, 750));
        }

        print.Length.ShouldBeLessThan(wide.Bytes.Length);

        CleanImage tall = ImageCleaner.Clean(TestImages.Jpeg(2000, 1500, orientation: 6, marked: true), Max).ShouldNotBeNull();
        using (SKBitmap shown = DecodeJpeg((await ImageSizer.PrintCopyAsync(tall.Bytes, tall.Width, tall.Height, 1000, Ct)).ShouldNotBeNull()))
        {
            (shown.Width, shown.Height).ShouldBe((750, 1000));
            shown.GetPixel(shown.Width * 3 / 4, shown.Height / 4).Red.ShouldBeLessThan((byte)40, "orientation 6 shows the stored top-left at the top right");
            shown.GetPixel(shown.Width / 4, shown.Height * 3 / 4).Red.ShouldBeGreaterThan((byte)100);
        }

        // Smaller than asked: as it is, not made larger.
        CleanImage small = ImageCleaner.Clean(TestImages.Png(600, 400), Max).ShouldNotBeNull();
        using (SKBitmap shown = DecodeJpeg((await ImageSizer.PrintCopyAsync(small.Bytes, 600, 400, 1000, Ct)).ShouldNotBeNull()))
        {
            (shown.Width, shown.Height).ShouldBe((600, 400));
        }
    }

    [Fact]
    public async Task A_print_copy_of_a_see_through_picture_is_on_white_and_a_file_that_is_not_one_gets_none()
    {
        using SKBitmap clear = new(new SKImageInfo(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        clear.Erase(SKColors.Transparent);
        using SKPixmap pixels = clear.PeekPixels();
        using SKData png = pixels.Encode(new SKPngEncoderOptions()).ShouldNotBeNull();
        using (SKBitmap shown = DecodeJpeg((await ImageSizer.PrintCopyAsync(png.ToArray(), 400, 400, 1000, Ct)).ShouldNotBeNull()))
        {
            SKColor middle = shown.GetPixel(200, 200);
            (middle.Red, middle.Green, middle.Blue).ShouldBe(((byte)255, (byte)255, (byte)255));
        }

        byte[] fake = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 8, 0x02, 0x58, 0x03, 0x20, 1, 1, 0x11, 0, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0x34, 0x56, 0xFF, 0xD9];
        (await ImageSizer.PrintCopyAsync(fake, 800, 600, 1000, Ct)).ShouldBeNull();
        (await ImageSizer.PrintCopyAsync(TestImages.Png(800, 600), 800, 601, 1000, Ct)).ShouldBeNull("another size than it says");
    }

    [Fact]
    public async Task A_picture_of_another_size_than_its_header_says_is_refused()
    {
        CleanImage clean = ImageCleaner.Clean(TestImages.Png(800, 600), Max).ShouldNotBeNull();
        (await ImageSizer.CopiesAsync(clean.Bytes, 800, 601, MediaSizes.All, Ct)).ShouldBeNull();
        (await ImageSizer.CopiesAsync(clean.Bytes, 600, 800, MediaSizes.All, Ct)).ShouldBeNull("not turned, as no orientation says so");
        BinaryPrimitives.ReadUInt32BigEndian(clean.Bytes.AsSpan(16)).ShouldBe(800u);
    }
}
