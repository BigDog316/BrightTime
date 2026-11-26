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

    private static List<ImageCopy>? Copies(ReadOnlySpan<byte> image, int shownWidth, int shownHeight, IReadOnlyList<int> sizes)
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

        int[] wanted = [.. sizes.Where(s => s > 0 && s < shownWidth).Distinct().Order()];

        // Only as large as the biggest copy needs; with none, as small as the codec will go.
        float scale = wanted.Length == 0 ? 0.125f : Math.Min(1f, (float)wanted[^1] / shownWidth);
        SKSizeI scaled = codec.GetScaledDimensions(scale);
        if ((turned ? scaled.Height : scaled.Width) < (wanted.Length == 0 ? 1 : wanted[^1]))
        {
            scaled = encoded;
        }

        SKImageInfo info = new(scaled.Width, scaled.Height, SKColorType.Rgba8888,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        using SKBitmap decoded = new(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            return null;
        }

        if (wanted.Length == 0)
        {
            return [];
        }

        using SKBitmap shown = Orient(decoded, origin);
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
