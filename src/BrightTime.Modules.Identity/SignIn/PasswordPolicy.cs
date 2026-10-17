using System.Collections.Frozen;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>
/// What a new password must be, following NIST SP 800-63B: long enough, not a known-common
/// password, not built from the account's own details. No composition rules ("one digit,
/// one symbol"): they make passwords harder to remember and barely harder to guess.
/// </summary>
/// <remarks>
/// The common-password list here is short and catches the passwords people reach for first.
/// The breached-password check (T21) is <see cref="IBreachedPasswordCheck"/>, applied by the
/// caller, because it may call out to a service.
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>Longer than NIST's floor of 8: these accounts see children's records.</summary>
    public const int MinimumLength = 12;

    public const int MaximumLength = 128;

    private static readonly FrozenSet<string> Common = new[]
    {
        "password", "password1", "password12", "password123", "password1234", "passw0rd", "p@ssword", "p@ssw0rd",
        "123456789012", "1234567890", "12345678910", "123456789", "qwertyuiop", "qwerty123456", "1q2w3e4r5t6y",
        "iloveyou", "letmein", "welcome", "welcome1", "welcome123", "changeme", "changeme123", "trustno1",
        "football", "baseball", "basketball", "superman", "batman", "princess", "sunshine", "shadow", "dragon",
        "monkey", "master", "michael", "jennifer", "jordan23", "abc123", "abcdefghijkl", "aaaaaaaaaaaa",
        "111111111111", "000000000000", "summer2024", "summer2025", "summer2026", "winter2025", "winter2026",
        "spring2026", "autumn2026", "fall2026", "newyork", "newyorkcity", "admin", "administrator", "teacher",
        "student", "school", "children", "volunteer", "community", "brighttime", "kettlecounty",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whatever is wrong with the password, as phrases; empty when it is acceptable.</summary>
    public static IReadOnlyList<Phrase> Check(string? password, string? email)
    {
        if (string.IsNullOrEmpty(password))
        {
            return [Phrase.Of("account.password.required")];
        }

        List<Phrase> problems = [];
        if (password.Length < MinimumLength)
        {
            problems.Add(Phrase.Of("account.password.too_short", ("min", MinimumLength)));
        }

        if (password.Length > MaximumLength)
        {
            problems.Add(Phrase.Of("account.password.too_long", ("max", MaximumLength)));
        }

        string folded = password.Trim();
        if (Common.Contains(folded) || Common.Contains(StripTrailingDigits(folded)) || folded.Distinct().Count() <= 2)
        {
            problems.Add(Phrase.Of("account.password.too_common"));
        }

        string? local = email?.Split('@')[0];
        if (local is { Length: >= 4 } && folded.Contains(local, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(Phrase.Of("account.password.from_email"));
        }

        return problems;
    }

    private static string StripTrailingDigits(string value) => value.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '!');
}

/// <summary>
/// Whether a password appears in known breaches (T21). The default says "not known"; a
/// deployment can switch on the k-anonymity check against Have I Been Pwned.
/// </summary>
public interface IBreachedPasswordCheck
{
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>No breach list configured.</summary>
public sealed class NoBreachedPasswordCheck : IBreachedPasswordCheck
{
    public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// Have I Been Pwned's range API: only the first five hex characters of the password's SHA-1
/// leave the server, never the password or its full hash. A failed lookup allows the
/// password rather than blocking someone from resetting theirs.
/// </summary>
public sealed class PwnedPasswordsCheck(HttpClient http) : IBreachedPasswordCheck
{
    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

#pragma warning disable CA5350 // SHA-1 is what the range API is keyed on; this is not a security hash here.
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        string prefix = hash[..5];
        string suffix = hash[5..];

        try
        {
            string body = await http.GetStringAsync(new Uri($"range/{prefix}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return body.Split('\n').Any(line => line.StartsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
