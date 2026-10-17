using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BrightTime.Seed;

/// <summary>
/// Identifiers derived from a stable name, so a re-run of the seed produces the same keys
/// and every insert can be <c>on conflict do nothing</c>.
/// </summary>
/// <remarks>
/// Laid out as UUIDv7, with a fixed timestamp and the random bits taken from SHA-256 of the
/// name, so the seed's keys look like every other key in the system. They are not secret
/// and not meant to be: everything seeded is fictional and public.
/// </remarks>
internal static class SeedIds
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Guid For(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("brighttime-seed/kettle/" + name), hash);

        Span<byte> bytes = stackalloc byte[16];
        long ms = Stamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        hash[..10].CopyTo(bytes[6..]);
        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));   // version 7
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));   // RFC 9562 variant

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>A stable integer from a name, for choices that must not depend on list order.</summary>
    public static int Hash(string name) =>
        BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(name))) & int.MaxValue;
}
