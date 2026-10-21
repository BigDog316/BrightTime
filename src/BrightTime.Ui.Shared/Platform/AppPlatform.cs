namespace BrightTime.Ui.Shared.Platform;

// What the shared UI needs from the device (docs/plan/01, "Mobile sharing model"). The MAUI host
// implements each; tests and any other host use the in-memory ones below. The shared UI never
// asks which platform it is on.

/// <summary>
/// Secrets kept by the operating system (the iOS keychain, the Android Keystore): the refresh
/// token and the install's device id. Nothing else.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    Task RemoveAsync(string key);
}

/// <summary>Plain settings that are not secret: the chosen language, whether onboarding was done.</summary>
public interface IAppPreferences
{
    string? Get(string key);

    void Set(string key, string? value);
}

/// <summary>A point on the map, which stays on the device.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// The device's position, asked for only when the person taps "near me": foreground, on demand,
/// never in the background (docs/plan/06). Null when they decline, or it is unavailable.
/// </summary>
public interface ILocationProvider
{
    Task<GeoPoint?> CurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>The system share sheet.</summary>
public interface IShareService
{
    Task ShareAsync(string title, Uri link);
}

/// <summary>Opens a link outside the app: a provider's website, a calendar file, the web's set-password page.</summary>
public interface IExternalLauncher
{
    Task OpenAsync(Uri link);
}

/// <summary>
/// Small documents kept on the device so a screen can show its last copy offline (saved items).
/// Not for anything a stolen phone should not reveal: rosters go in the sealed store (D52).
/// </summary>
public interface IOfflineStore
{
    Task SaveAsync(string key, string json);

    Task<string?> LoadAsync(string key);

    Task ClearAsync();
}

/// <summary>
/// Raw bytes in the app's private storage, for the sealed store to keep its ciphertext in (D52).
/// It never sees plaintext; the host only stores and deletes.
/// </summary>
public interface IBlobStore
{
    Task WriteAsync(string name, byte[] bytes);

    Task<byte[]?> ReadAsync(string name);

    Task DeleteAllAsync();
}

/// <summary>This device's push token, when a push provider is configured.</summary>
public readonly record struct PushRegistration(string Platform, string Token);

/// <summary>Asks the platform for a push token. Null until Firebase and APNs are set up, or when declined.</summary>
public interface IPushRegistrar
{
    Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The system browser for a sign-in that happens on the web (D41): opens <c>start</c>, waits for
/// the redirect to the app's <c>callback</c> scheme, and gives back its fragment's values. Null when
/// the person closed the browser.
/// </summary>
public interface IExternalAuthenticator
{
    bool IsSupported { get; }

    Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri start, Uri callback, CancellationToken cancellationToken = default);
}

/// <summary>No system browser to sign in with.</summary>
public sealed class NoExternalAuthenticator : IExternalAuthenticator
{
    public bool IsSupported => false;

    public Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri start, Uri callback, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
}

/// <summary>What a passkey prompt came to: the credential, as WebAuthn JSON, or why there is none.</summary>
public sealed record PasskeyOutcome(string? CredentialJson, bool Cancelled = false)
{
    public static PasskeyOutcome Declined { get; } = new(CredentialJson: null, Cancelled: true);

    public static PasskeyOutcome Failed { get; } = new(CredentialJson: null);
}

/// <summary>
/// The platform's passkeys (Android's Credential Manager, iOS's AuthenticationServices): takes the
/// server's WebAuthn options as JSON and gives back the authenticator's answer as JSON.
/// </summary>
public interface IPasskeyAuthenticator
{
    /// <summary>False where the platform has no passkeys; the screens then offer none.</summary>
    bool IsSupported { get; }

    Task<PasskeyOutcome> CreateAsync(string optionsJson, CancellationToken cancellationToken = default);

    Task<PasskeyOutcome> GetAsync(string optionsJson, CancellationToken cancellationToken = default);
}

/// <summary>No passkeys on this platform.</summary>
public sealed class NoPasskeys : IPasskeyAuthenticator
{
    public bool IsSupported => false;

    public Task<PasskeyOutcome> CreateAsync(string optionsJson, CancellationToken cancellationToken = default) => Task.FromResult(PasskeyOutcome.Failed);

    public Task<PasskeyOutcome> GetAsync(string optionsJson, CancellationToken cancellationToken = default) => Task.FromResult(PasskeyOutcome.Failed);
}

/// <summary>An <see cref="ISecretStore"/> that forgets on exit: for tests, and a host with no keychain.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class MemoryPreferences : IAppPreferences
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
    }
}

public sealed class MemoryOfflineStore : IOfflineStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task SaveAsync(string key, string json)
    {
        _values[key] = json;
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));

    public Task ClearAsync()
    {
        _values.Clear();
        return Task.CompletedTask;
    }
}

public sealed class MemoryBlobStore : IBlobStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>What is stored, for a test to look inside.</summary>
    public IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public Task WriteAsync(string name, byte[] bytes)
    {
        _blobs[name] = bytes;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string name) => Task.FromResult(_blobs.GetValueOrDefault(name));

    public Task DeleteAllAsync()
    {
        _blobs.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>No location: "near me" says it is unavailable.</summary>
public sealed class NoLocation : ILocationProvider
{
    public Task<GeoPoint?> CurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<GeoPoint?>(null);
}

/// <summary>No push provider yet: nothing is registered, and the inbox still works.</summary>
public sealed class NoPush : IPushRegistrar
{
    public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default) => Task.FromResult<PushRegistration?>(null);
}
