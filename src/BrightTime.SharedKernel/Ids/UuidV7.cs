namespace BrightTime.SharedKernel.Ids;

/// <summary>
/// Primary-key generation for the whole solution.
/// </summary>
/// <remarks>
/// <para>
/// Every identifier is generated here, in the application, as a UUIDv7. The
/// schema deliberately declares no <c>DEFAULT gen_random_uuid()</c>: a v4 key
/// is random, so inserts scatter across the whole B-tree and dirty a page per
/// row. On append-heavy tables — <c>attendance_record</c>, <c>audit_event</c>,
/// <c>listing_occurrence</c> — that is the difference between appending to the
/// right edge of an index and rewriting it. Keeping the default out of the DDL
/// means an accidental server-side v4 shows up as a not-null violation rather
/// than as a slow, invisible regression months later.
/// </para>
/// <para>
/// A UUIDv7 leaks its creation time to the millisecond. That is acceptable for
/// our rows (creation time is stored in <c>created_at</c> anyway) but it is
/// <em>not</em> acceptable for anything a third party can see and correlate.
/// Public-facing opaque values — verification slugs, check-in tokens, share
/// links — use <see cref="NewOpaqueToken"/> instead.
/// </para>
/// </remarks>
public static class UuidV7
{
    /// <summary>A time-ordered identifier for a database row.</summary>
    public static Guid NewId() => Guid.CreateVersion7();

    /// <summary>A time-ordered identifier stamped with a specific instant, for deterministic tests and backfills.</summary>
    public static Guid NewId(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);

    /// <summary>
    /// A 160-bit URL-safe random token for anything externally visible, where a
    /// guessable or time-correlatable value would be a weakness.
    /// </summary>
    public static string NewOpaqueToken()
    {
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
