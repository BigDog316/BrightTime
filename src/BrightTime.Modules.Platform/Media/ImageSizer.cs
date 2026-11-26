using SkiaSharp;

namespace BrightTime.Modules.Platform.Media;

/// <summary>A smaller copy of an image: the size asked for, its WebP bytes, and its size in pixels.</summary>
public sealed record ImageCopy(int Size, byte[] Bytes, int Width, int Height);

/// <summary>
/// Decodes a cleaned PNG or JPEG with Skia and makes its smaller copies (D106). The decode is also
/// the check that the file is a picture at all: one that doesn't decode whole, or decodes to
/// another size than <see cref="BrightTime.SharedKernel.Media.ImageCleaner"/> read from its header,
/// isn't one.
/// </summary>
/// <remarks>
/// <para>
/// A copy is as wide as its size and as tall as that makes it, turned as the image is shown (a
/// JPEG's orientation is applied, since WebP carries none), and lossy WebP at quality 80. It's
/// made by halving, then one cubic step, so a large photo isn't aliased on the way down.
/// </para>
/// <para>
/// A JPEG is decoded only as large as the biggest copy needs, an eighth at the least; a PNG is
/// decoded whole, up to 6,000 pixels a side (144 MB). So at most two decodes run at once in a
/// process, and an upload waits for its turn.
/// </para>
/// </remarks>
public static class ImageSizer
{
    public const string ContentType = "image/webp";

    public const float Quality = 80;

    /// <summary>A print copy's JPEG quality (D107).</summary>
    public const int PrintQuality = 85;

    private static readonly SemaphoreSlim Decoding = new(2, 2);

    /// <summary>
    /// The copies of the image at each of <paramref name="sizes"/> it's wider than, smallest first;
    /// none asked for, it's only decoded. Null when it isn't a picture of
    /// <paramref name="shownWidth"/> by <paramref name="shownHeight"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ImageCopy>?> CopiesAsync(
        ReadOnlyMemory<byte> image, int shownWidth, int shownHeight, IReadOnlyList<int> sizes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        await Decoding.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Copies(image.Span, shownWidth, shownHeight, sizes);
        }
        finally
        {
            Decoding.Release();
        }
    }

    /// <summary>
    /// The image as a JPEG for print (D107): its longest side at most <paramref name="longest"/>,
    /// turned as it's shown, on white where it's see-through, at <see cref="PrintQuality"/>. Made
    /// when asked and never kept. Null when it isn't a picture of <paramref name="shownWidth"/> by
    /// <paramref name="shownHeight"/>.
    /// </summary>
    public static async Task<byte[]?> PrintCopyAsync(
        ReadOnlyMemory<byte> image, int shownWidth, int shownHeight, int longest, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(longest);
        await Decoding.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return PrintCopy(image.Span, shownWidth, shownHeight, longest);
        }
        finally
        {
            Decoding.Release();
        }
    }

    private static byte[]? PrintCopy(ReadOnlySpan<byte> image, int shownWidth, int shownHeight, int longest)
    {
        if (shownWidth <= 0 || shownHeight <= 0)
        {
            return null;
        }

        double fit = Math.Min(1d, (double)longest / Math.Max(shownWidth, shownHeight));
        int width = Math.Max(1, (int)Math.Round(shownWidth * fit, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(shownHeight * fit, MidpointRounding.AwayFromZero));
        if (Decode(image, shownWidth, shownHeight, width) is not { } decoded)
        {
            return null;
        }

        using SKBitmap stored = decoded.Bitmap;
        using SKBitmap shown = Orient(stored, decoded.Origin);
        using SKBitmap? smaller = shown.Width == width && shown.Height == height ? null : Shrink(shown, width, height);
        SKBitmap copy = smaller ?? shown;
        using SKBitmap flat = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(copy, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            canvas.Flush();
        }

        using SKPixmap pixels = flat.PeekPixels();
        using SKData jpeg = pixels.Encode(new SKJpegEncoderOptions(PrintQuality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
            ?? throw new InvalidOperationException("Skia couldn't encode JPEG.");
        return jpeg.ToArray();
    }

    private static List<ImageCopy>? Copies(ReadOnlySpan<byte> image, int shownWidth, int shownHeight, IReadOnlyList<int> sizes)
    {
        int[] wanted = [.. sizes.Where(s => s > 0 && s < shownWidth).Distinct().Order()];

        // Only as large as the biggest copy needs; with none, as small as the codec will go.
        if (Decode(image, shownWidth, shownHeight, wanted.Length == 0 ? 0 : wanted[^1]) is not { } decoded)
        {
            return null;
        }

        using SKBitmap stored = decoded.Bitmap;
        if (wanted.Length == 0)
        {
            return [];
        }

        using SKBitmap shown = Orient(stored, decoded.Origin);
        List<ImageCopy> copies = [];
        foreach (int size in wanted)
        {
            // As tall as the original makes it: a scaled decode rounds its sides.
            int height = Math.Max(1, (int)Math.Round((double)shownHeight * size / shownWidth, MidpointRounding.AwayFromZero));
            using SKBitmap copy = Shrink(shown, size, height);
            using SKPixmap pixels = copy.PeekPixels();
            using SKData webp = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, Quality))
                ?? throw new InvalidOperationException("Skia couldn't encode WebP.");
            copies.Add(new ImageCopy(size, webp.ToArray(), copy.Width, copy.Height));
        }

        return copies;
    }

    /// <summary>
    /// The image decoded as it's stored, at least <paramref name="width"/> wide as it's shown (a
    /// JPEG no larger than that needs; with 0, as small as the codec will go), and its orientation.
    /// Null when it doesn't decode whole, or isn't <paramref name="shownWidth"/> by
    /// <paramref name="shownHeight"/> as it's shown.
    /// </summary>
    private static (SKBitmap Bitmap, SKEncodedOrigin Origin)? Decode(ReadOnlySpan<byte> image, int shownWidth, int shownHeight, int width)
    {
        using SKData data = SKData.CreateCopy(image);
        using SKCodec? codec = SKCodec.Create(data);
#pragma warning disable CA1508 // Annotated as never null, but it is for anything Skia can't read.
        if (codec is null)
        {
            return null;
        }
#pragma warning restore CA1508

        SKEncodedOrigin origin = codec.EncodedOrigin;
        bool turned = Turned(origin);
        SKSizeI encoded = codec.Info.Size;
        if ((turned ? encoded.Height : encoded.Width) != shownWidth || (turned ? encoded.Width : encoded.Height) != shownHeight)
        {
            return null;
        }

        float scale = width <= 0 ? 0.125f : Math.Min(1f, (float)width / shownWidth);
        SKSizeI scaled = codec.GetScaledDimensions(scale);
        if ((turned ? scaled.Height : scaled.Width) < Math.Max(1, width))
        {
            scaled = encoded;
        }

        SKImageInfo info = new(scaled.Width, scaled.Height, SKColorType.Rgba8888,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        SKBitmap decoded = new(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            decoded.Dispose();
            return null;
        }

        return (decoded, origin);
    }

    /// <summary>Whether the image is stored on its side: shown, its width is its stored height.</summary>
    private static bool Turned(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>The image as it's shown: a copy, turned and flipped as its orientation says (EXIF 1 to 8).</summary>
    internal static SKBitmap Orient(SKBitmap stored, SKEncodedOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(stored);
        float w = stored.Width;
        float h = stored.Height;

        // Where each stored point lands: x' = scaleX·x + skewX·y + transX, y' = skewY·x + scaleY·y + transY.
        SKMatrix m = origin switch
        {
            SKEncodedOrigin.TopRight => new(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        bool turned = Turned(origin);
        SKBitmap shown = new(stored.Info.WithSize(turned ? stored.Height : stored.Width, turned ? stored.Width : stored.Height));
        using SKCanvas canvas = new(shown);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(stored, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        canvas.Flush();
        return shown;
    }

    /// <summary>The image at <paramref name="width"/> by <paramref name="height"/>: halved while it's twice that or more, then one cubic step.</summary>
    private static SKBitmap Shrink(SKBitmap image, int width, int height)
    {
        SKBitmap? halved = null;
        try
        {
            SKBitmap current = image;
            while (current.Width >= width * 2)
            {
                SKBitmap next = current.Resize(current.Info.WithSize(current.Width / 2, Math.Max(1, current.Height / 2)), new SKSamplingOptions(SKFilterMode.Linear))
                    ?? throw new InvalidOperationException("Skia couldn't halve the image.");
                halved?.Dispose();
                halved = next;
                current = next;
            }

            return current.Resize(current.Info.WithSize(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new InvalidOperationException("Skia couldn't resize the image.");
        }
        finally
        {
            halved?.Dispose();
        }
    }
}
