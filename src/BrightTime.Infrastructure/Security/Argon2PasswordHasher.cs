using Isopoh.Cryptography.Argon2;

namespace BrightTime.Infrastructure.Security;

/// <summary>The result of checking a password against a stored hash.</summary>
public readonly record struct PasswordCheck(bool IsValid, bool NeedsRehash);

/// <summary>
/// Argon2id password hashing (threat model T21), in the PHC string format so the parameters
/// travel with each hash.
/// </summary>
/// <remarks>
/// <para>
/// Parameters follow the OWASP minimum for Argon2id: 19 MiB of memory, two passes, one lane.
/// A hash made with weaker parameters still verifies and reports <see cref="PasswordCheck.NeedsRehash"/>,
/// so raising them later upgrades each account at its next sign-in.
/// </para>
/// <para>
/// <see cref="VerifyAgainstNothing"/> spends the same work when there is no account, so an
/// attacker cannot tell a registered email from an unregistered one by response time.
/// </para>
/// </remarks>
public static class Argon2PasswordHasher
{
    public const int MemoryKiB = 19_456;
    public const int Passes = 2;
    public const int Lanes = 1;
    private const int HashBytes = 32;

    private static readonly string CurrentPrefix = $"$argon2id$v=19$m={MemoryKiB},t={Passes},p={Lanes}$";

    // A real hash of a random string, made once, so the dummy verification costs what a real one does.
    private static readonly Lazy<string> Decoy = new(() => Hash(Guid.NewGuid().ToString("N")));

    public static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Hash(password);
    }

    public static PasswordCheck Verify(string? encoded, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (string.IsNullOrEmpty(encoded) || !encoded.StartsWith("$argon2", StringComparison.Ordinal))
        {
            VerifyAgainstNothing(password);
            return new PasswordCheck(false, false);
        }

        bool valid = Argon2.Verify(encoded, password);
        return new PasswordCheck(valid, valid && !encoded.StartsWith(CurrentPrefix, StringComparison.Ordinal));
    }

    /// <summary>Does a full verification that cannot succeed, to keep timing uniform.</summary>
    public static void VerifyAgainstNothing(string password) => _ = Argon2.Verify(Decoy.Value, password ?? string.Empty);

    private static string Hash(string password) =>
        Argon2.Hash(password, Passes, MemoryKiB, Lanes, Argon2Type.HybridAddressing, HashBytes);
}
