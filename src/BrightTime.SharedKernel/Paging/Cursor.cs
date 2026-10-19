using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace BrightTime.SharedKernel.Paging;

/// <summary>
/// An opaque keyset cursor: the last row's sort value plus its id, and the instant the
/// sort was measured at when it depends on the time.
/// </summary>
/// <remarks>
/// <para>
/// Keyset, never OFFSET. The finder sorts by a computed relevance score, and
/// <c>OFFSET 960</c> on a scored set makes the database re-match and re-sort
/// the entire result to return twenty-four rows. Keyset also keeps a user's
/// page stable while a provider publishes something mid-session.
/// </para>
/// <para>
/// <see cref="AsOf"/> exists because the relevance score rises as a listing's start
/// approaches. Measured afresh on each page, scores drift past the previous page's last
/// value between requests, and rows are skipped or repeated. Every page after the first
/// measures at the first page's instant instead.
/// </para>
/// </remarks>
public readonly record struct Cursor(string SortValue, Guid LastId, DateTimeOffset? AsOf = null)
{
    public string Encode()
    {
        string payload = AsOf is { } asOf
            ? string.Create(CultureInfo.InvariantCulture, $"{SortValue}|{LastId:D}|{asOf.ToUnixTimeMilliseconds()}")
            : string.Create(CultureInfo.InvariantCulture, $"{SortValue}|{LastId:D}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>An instant at the precision a cursor keeps, so the first page and the rest agree exactly.</summary>
    public static DateTimeOffset Precision(DateTimeOffset instant) =>
        DateTimeOffset.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());

    public static bool TryDecode(string? encoded, out Cursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));

            // Split from the right, so a separator inside the sort value survives. With an
            // instant the last part is a number and the id comes before it; without one, as in
            // cursors issued before the instant existed, the last part is the id.
            int last = payload.LastIndexOf('|');
            if (last <= 0)
            {
                return false;
            }

            if (Guid.TryParse(payload.AsSpan(last + 1), out Guid id))
            {
                cursor = new Cursor(payload[..last], id);
                return true;
            }

            int previous = payload.LastIndexOf('|', last - 1);
            if (previous <= 0
                || !long.TryParse(payload.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds)
                || !Guid.TryParse(payload.AsSpan(previous + 1, last - previous - 1), out id))
            {
                return false;
            }

            cursor = new Cursor(payload[..previous], id, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
            return true;
        }
        catch (FormatException)
        {
            // A malformed cursor is a client problem, not a server error: the
            // caller falls back to the first page.
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // An instant outside DateTimeOffset's range is malformed too.
            return false;
        }
    }
}
