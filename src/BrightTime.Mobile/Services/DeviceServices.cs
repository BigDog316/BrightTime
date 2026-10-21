using System.Security.Cryptography;
using System.Text;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Mobile.Services;

/// <summary>The API client: the platform's own HTTP stack, and in development the region's Host header.</summary>
internal static class ApiClient
{
    public static HttpClient Create()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };

        // The local stack serves every region from one address and tells them apart by host
        // (TenantRequestMiddleware), which the emulator cannot name; a real region's address is its host.
        if (BuildConfig.HostHeader.Length > 0)
        {
            client.DefaultRequestHeaders.Host = BuildConfig.HostHeader;
        }

        return client;
    }
}

/// <summary>Secrets in the iOS keychain or the Android Keystore (MAUI's SecureStorage).</summary>
internal sealed class DeviceSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class DevicePreferences : IAppPreferences
{
    public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            Preferences.Default.Remove(key);
        }
        else
        {
            Preferences.Default.Set(key, value);
        }
    }
}

/// <summary>
/// JSON files in the app's private data folder, which no other app can read. Saved listings are
/// public data plus the fact of saving them; nothing here needs the encrypted store (M3's rosters).
/// </summary>
internal sealed class DeviceOfflineStore : IOfflineStore
{
    private static string Folder => Path.Combine(FileSystem.AppDataDirectory, "offline");

    public async Task SaveAsync(string key, string json)
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(PathFor(key), json, Encoding.UTF8).ConfigureAwait(false);
    }

    public async Task<string?> LoadAsync(string key)
    {
        string path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false) : null;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    // Keys are the app's own constants; hashed anyway, so no key can name a path outside the folder.
    private static string PathFor(string key) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
}

/// <summary>
/// The sealed store's ciphertext (D52), in its own folder of the app's private data, kept out of
/// the platform's cloud backups. Only ciphertext arrives here; the key is in the keychain.
/// </summary>
internal sealed class DeviceBlobStore : IBlobStore
{
    private static string Folder => Path.Combine(FileSystem.AppDataDirectory, "sealed");

    public async Task WriteAsync(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
#if IOS
        // Android's backups are off (android:allowBackup="false"); iOS backs up app data unless told not to.
        Foundation.NSUrl.FromFilename(Folder).SetResource(Foundation.NSUrl.IsExcludedFromBackupKey, Foundation.NSNumber.FromBoolean(true));
#endif
        string path = PathFor(name);
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<byte[]?> ReadAsync(string name)
    {
        string path = PathFor(name);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path).ConfigureAwait(false) : null;
    }

    public Task DeleteAllAsync()
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string PathFor(string name) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))) + ".bin");
}

/// <summary>
/// The phone's position, asked for only when the person taps "near me" (docs/plan/06): the
/// when-in-use permission, a coarse fix, and nothing in the background.
/// </summary>
internal sealed class DeviceLocation : ILocationProvider
{
    public async Task<GeoPoint?> CurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            PermissionStatus status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                return null;
            }

            Location? here = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Low, TimeSpan.FromSeconds(10)), cancellationToken);
            return here is null ? null : new GeoPoint(here.Latitude, here.Longitude);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException or PermissionException or TaskCanceledException)
        {
            return null;
        }
    }
}

internal sealed class DeviceShare : IShareService
{
    public Task ShareAsync(string title, Uri link) =>
        Share.Default.RequestAsync(new ShareTextRequest { Title = title, Uri = link.AbsoluteUri });
}

/// <summary>Opens a link with the system: the browser, or the app that handles it (a calendar file).</summary>
internal sealed class DeviceLauncher : IExternalLauncher
{
    public Task OpenAsync(Uri link) => Launcher.Default.OpenAsync(link);
}
