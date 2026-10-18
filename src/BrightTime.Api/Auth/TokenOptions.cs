using System.Security.Cryptography;
using BrightTime.Modules.Identity.SignIn;

namespace BrightTime.Api.Auth;

/// <summary>A key access tokens are signed with, named so a token says which one.</summary>
public sealed class SigningKeyOptions
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Base64, 32 bytes or more.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>How the API's tokens are signed and how long they and their sessions last.</summary>
public sealed class TokenOptions
{
    public const string SectionName = "Auth:Tokens";

    /// <summary>The audience every access token names, and the only one the API accepts.</summary>
    public const string Audience = "brighttime-api";

    /// <summary>
    /// Signing keys, the first signs new tokens and all of them verify, so a key is rotated by
    /// adding the new one first and removing the old one once its tokens have expired.
    /// </summary>
    public IList<SigningKeyOptions> SigningKeys { get; init; } = [];

    /// <summary>Access tokens are short: an account change or "sign out everywhere" reaches them at the next request anyway (the stamp check), and a stolen one is soon dead.</summary>
    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>An adult's session ends after this long unused.</summary>
    public TimeSpan RefreshIdle { get; set; } = TimeSpan.FromDays(30);

    /// <summary>A youth's session ends after this long unused (T14: shared devices).</summary>
    public TimeSpan YouthRefreshIdle { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>No session outlives this, however active.</summary>
    public TimeSpan RefreshAbsolute { get; set; } = TimeSpan.FromDays(90);

    public SessionLifetime LifetimeFor(string accountKind) =>
        new(accountKind == "youth" ? YouthRefreshIdle : RefreshIdle, RefreshAbsolute);

    /// <summary>
    /// The keys, checked. Development without any gets one made up per run, so tokens die with
    /// the process; anywhere else a missing or short key stops startup.
    /// </summary>
    public IReadOnlyList<(string Id, byte[] Key)> Keys(bool development)
    {
        List<(string, byte[])> keys = [];
        foreach (SigningKeyOptions k in SigningKeys)
        {
            byte[] bytes = Convert.FromBase64String(k.Key);
            if (bytes.Length < 32 || string.IsNullOrWhiteSpace(k.Id))
            {
                throw new InvalidOperationException("Each Auth:Tokens:SigningKeys entry needs an Id and a base64 Key of 32 bytes or more.");
            }

            keys.Add((k.Id, bytes));
        }

        if (keys.Count == 0)
        {
            if (!development)
            {
                throw new InvalidOperationException("Auth:Tokens:SigningKeys is required: access tokens are signed with it.");
            }

            keys.Add(("dev-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)), RandomNumberGenerator.GetBytes(32)));
        }

        return keys;
    }
}
