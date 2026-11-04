using System.Collections;
using System.Globalization;
using System.Text;
using QRCoder;

namespace BrightTime.Ui.Shared;

/// <summary>
/// Draws a QR code as one SVG path (D76): QRCoder encodes it, at error correction level M, and each
/// row's run of dark modules becomes a rectangle. The size includes the four-module quiet zone a
/// scanner needs, so the caller draws a white square that size and the path over it.
/// </summary>
public static class QrDrawing
{
    public static (int Size, string Path) Draw(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        List<BitArray> rows = data.ModuleMatrix;
        StringBuilder path = new();
        for (int y = 0; y < rows.Count; y++)
        {
            BitArray row = rows[y];
            for (int x = 0; x < row.Length; x++)
            {
                if (!row[x])
                {
                    continue;
                }

                int start = x;
                while (x + 1 < row.Length && row[x + 1])
                {
                    x++;
                }

                int run = x - start + 1;
                path.Append(CultureInfo.InvariantCulture, $"M{start} {y}h{run}v1h-{run}z");
            }
        }

        return (rows.Count, path.ToString());
    }
}
