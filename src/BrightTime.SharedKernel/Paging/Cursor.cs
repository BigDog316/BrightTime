using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace BrightTime.SharedKernel.Paging;

/// <summary>
/// An opaque keyset cursor: the last row's sort value plus its id.
/// </summary>
/// <remarks>
/// Keyset, never OFFSET. The finder sorts by a computed relevance score, and
/// <c>OFFSET 960</c> on a scored set makes the database re-match and re-sort
/// the entire result to return twenty-four rows. Keyset also keeps a user's
/// page stable while a provider publishes something mid-session.
/// </remarks>
public readonly record struct Cursor(string SortValue, Guid LastId)
{
    public string Encode()
    {
        string payload = string.Create(CultureInfo.InvariantCulture, $"{SortValue}|{LastId:D}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

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
            int separator = payload.LastIndexOf('|');
            if (separator <= 0 || !Guid.TryParse(payload.AsSpan(separator + 1), out Guid id))
            {
                return false;
            }

            cursor = new Cursor(payload[..separator], id);
            return true;
        }
        catch (FormatException)
        {
            // A malformed cursor is a client problem, not a server error: the
            // caller falls back to the first page.
            return false;
        }
    }
}
