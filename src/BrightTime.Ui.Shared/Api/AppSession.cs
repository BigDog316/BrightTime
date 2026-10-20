using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Ui.Shared.Api;

/// <summary>
/// Who is signed in on this device, and the tokens that say so (D28): a short access token in
/// memory, and a rotating refresh token.
/// </summary>
/// <remarks>
/// <para>
/// An adult's refresh token is kept in the operating system's secret store, so the app stays
/// signed in. A young person's is not (docs/plan/06: no "remember me" for youth, because the
/// device is often shared): closing the app signs them out, and the server ends their session
/// after 30 minutes unused anyway.
/// </para>
/// <para>
/// Refreshing is serialised: two requests that find the access token stale refresh once, since
/// a refresh token used twice ends the whole family (D28's theft detection).
/// </para>
/// </remarks>
public sealed class AppSession(HttpClient http, ISecretStore secrets, TimeProvider time) : IDisposable
{
    public const string RefreshTokenKey = "brighttime.refresh_token";
    public const string DeviceIdKey = "brighttime.device_id";

    /// <summary>Refresh this long before the access token runs out, so a request never carries a dead one.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessExpiresAt;

    /// <summary>The signed-in person, or null.</summary>
    public Me? Me { get; private set; }

    /// <summary>What the signed-in account may do; null until read, and for no one signed in.</summary>
    public Capabilities? Capabilities { get; private set; }

    public bool IsSignedIn => Me is not null;

    /// <summary>Raised when someone signs in or out, or their capabilities change.</summary>
    public event EventHandler? Changed;

    /// <summary>On start: an adult's kept refresh token signs them back in, if it still works.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (await secrets.GetAsync(RefreshTokenKey).ConfigureAwait(false) is not { Length: > 0 } kept)
        {
            return;
        }

        _refreshToken = kept;
        if (!await RefreshAsync(cancellationToken).ConfigureAwait(false))
        {
            await ForgetAsync().ConfigureAwait(false);
            return;
        }

        await LoadPersonAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs in with an email address and password. Null on success, else what went wrong.</summary>
    public async Task<ApiProblem?> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        string device = await DeviceIdAsync().ConfigureAwait(false);
        ApiResult<TokenPair> issued = await PostAsync<TokenPair>("auth/token", new { email, password, deviceId = device }, cancellationToken).ConfigureAwait(false);
        if (!issued.IsSuccess)
        {
            return issued.Problem;
        }

        Accept(issued.Value!);
        if (!await LoadPersonAsync(cancellationToken).ConfigureAwait(false))
        {
            await ForgetAsync().ConfigureAwait(false);
            return new ApiProblem(ApiProblem.UnreadableType, null, 0);
        }

        return null;
    }

    /// <summary>
    /// Signs in with a passkey (D38): the server's options go to the platform's prompt, and its
    /// answer back. Null on success; <see cref="PasskeyDeclined"/> when the person closed the prompt.
    /// </summary>
    public async Task<ApiProblem?> SignInWithPasskeyAsync(IPasskeyAuthenticator authenticator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        ApiResult<PasskeyCeremony> ceremony = await PostAsync<PasskeyCeremony>("auth/passkeys/assert/options", new { }, cancellationToken).ConfigureAwait(false);
        if (!ceremony.IsSuccess)
        {
            return ceremony.Problem;
        }

        PasskeyOutcome answer = await authenticator.GetAsync(ceremony.Value!.Options.GetRawText(), cancellationToken).ConfigureAwait(false);
        if (answer.CredentialJson is not { } credential)
        {
            return answer.Cancelled ? PasskeyDeclined : new ApiProblem(ApiProblem.Prefix + "passkey.failed", null, 0);
        }

        string device = await DeviceIdAsync().ConfigureAwait(false);
        using JsonDocument parsed = JsonDocument.Parse(credential);
        ApiResult<TokenPair> issued = await PostAsync<TokenPair>(
            "auth/passkeys/assert", new { state = ceremony.Value.State, credential = parsed.RootElement, deviceId = device }, cancellationToken).ConfigureAwait(false);
        if (!issued.IsSuccess)
        {
            return issued.Problem;
        }

        Accept(issued.Value!);
        if (!await LoadPersonAsync(cancellationToken).ConfigureAwait(false))
        {
            await ForgetAsync().ConfigureAwait(false);
            return new ApiProblem(ApiProblem.UnreadableType, null, 0);
        }

        return null;
    }

    /// <summary>
    /// Signs in with an external provider (D41): the system browser runs the provider's flow on the
    /// web, which hands back a one-time code; the code and this sign-in's PKCE verifier buy tokens.
    /// A state that is not the one sent, or a code without the verifier, gets nothing.
    /// </summary>
    public async Task<ApiProblem?> SignInExternallyAsync(IExternalAuthenticator browser, AppEndpoints endpoints, string provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(endpoints);
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(24));

        IReadOnlyDictionary<string, string>? answer = await browser.AuthenticateAsync(
            endpoints.ExternalStart(provider, challenge, state), endpoints.AppCallback, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return PasskeyDeclined;
        }

        if (!answer.TryGetValue("state", out string? returned) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returned), Encoding.ASCII.GetBytes(state)))
        {
            return new ApiProblem(ApiProblem.Prefix + "external.failed", null, 0);
        }

        if (answer.TryGetValue("error", out string? error))
        {
            return new ApiProblem(ApiProblem.Prefix + (error == "unconnected" ? "external.unconnected" : "external.failed"), null, 0);
        }

        string device = await DeviceIdAsync().ConfigureAwait(false);
        ApiResult<TokenPair> issued = await PostAsync<TokenPair>(
            "auth/external/token", new { code = answer.GetValueOrDefault("code"), verifier, deviceId = device }, cancellationToken).ConfigureAwait(false);
        if (!issued.IsSuccess)
        {
            return issued.Problem!.Type == ApiProblem.OfflineType ? issued.Problem : new ApiProblem(ApiProblem.Prefix + "external.failed", null, 0);
        }

        Accept(issued.Value!);
        if (!await LoadPersonAsync(cancellationToken).ConfigureAwait(false))
        {
            await ForgetAsync().ConfigureAwait(false);
            return new ApiProblem(ApiProblem.UnreadableType, null, 0);
        }

        return null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The person closed the passkey prompt: nothing to say.</summary>
    public static ApiProblem PasskeyDeclined { get; } = new("urn:brighttime:app:passkey-declined", null, 0);

    /// <summary>Signs out here: the refresh token is ended on the server and forgotten on the device.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshToken is { } refresh)
        {
            // Whether or not it reaches the server, the device forgets.
            await PostAsync<JsonElement>("auth/logout", new { refreshToken = refresh }, cancellationToken).ConfigureAwait(false);
        }

        await ForgetAsync().ConfigureAwait(false);
    }

    /// <summary>A current access token, refreshed first if it is about to run out; null when no one is signed in.</summary>
    public async Task<string?> AccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken is not null && time.GetUtcNow() < _accessExpiresAt - RefreshMargin)
        {
            return _accessToken;
        }

        return _refreshToken is null || !await RefreshAsync(cancellationToken).ConfigureAwait(false) ? null : _accessToken;
    }

    /// <summary>
    /// Exchanges the refresh token for a new pair. False when it no longer works (expired, used,
    /// or the session was ended elsewhere), which signs the person out.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        string? before = _refreshToken;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request refreshed while this one waited: use what it got.
            if (_refreshToken != before && _accessToken is not null)
            {
                return true;
            }

            if (_refreshToken is not { } refresh)
            {
                return false;
            }

            ApiResult<TokenPair> renewed = await PostAsync<TokenPair>("auth/token/refresh", new { refreshToken = refresh }, cancellationToken).ConfigureAwait(false);
            if (renewed.IsSuccess)
            {
                Accept(renewed.Value!);
                await KeepAsync().ConfigureAwait(false);
                return true;
            }

            // Offline is not signed out: keep the token for when the network returns.
            if (renewed.Problem!.Type != ApiProblem.OfflineType)
            {
                await ForgetAsync().ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads what the account may do (<c>GET /me/capabilities</c>), after sign-in and whenever consent may have changed.</summary>
    public async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (await AccessTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "me/capabilities");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Capabilities = await response.Content.ReadFromJsonAsync<Capabilities>(Json, cancellationToken).ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (HttpRequestException)
        {
            // Offline: keep what was read last; the server enforces regardless.
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<bool> LoadPersonAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not { } token)
        {
            return false;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            Me = await response.Content.ReadFromJsonAsync<Me>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return false;
        }

        await KeepAsync().ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return Me is not null;
    }

    private void Accept(TokenPair pair)
    {
        _accessToken = pair.AccessToken;
        _refreshToken = pair.RefreshToken;
        _accessExpiresAt = time.GetUtcNow().AddSeconds(pair.ExpiresIn);
    }

    /// <summary>An adult's refresh token is kept; a young person's never is.</summary>
    private async Task KeepAsync()
    {
        if (Me is { IsAdult: true } && _refreshToken is { } refresh)
        {
            await secrets.SetAsync(RefreshTokenKey, refresh).ConfigureAwait(false);
        }
        else
        {
            await secrets.RemoveAsync(RefreshTokenKey).ConfigureAwait(false);
        }
    }

    private async Task ForgetAsync()
    {
        bool was = Me is not null;
        _accessToken = null;
        _refreshToken = null;
        Me = null;
        Capabilities = null;
        await secrets.RemoveAsync(RefreshTokenKey).ConfigureAwait(false);
        if (was)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A random id for this install, which the server ties a refresh-token family to. Not a hardware id.</summary>
    private async Task<string> DeviceIdAsync()
    {
        if (await secrets.GetAsync(DeviceIdKey).ConfigureAwait(false) is { Length: > 0 } known)
        {
            return known;
        }

        string id = Guid.NewGuid().ToString("N");
        await secrets.SetAsync(DeviceIdKey, id).ConfigureAwait(false);
        return id;
    }

    private async Task<ApiResult<T>> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(path, body, Json, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.NoContent
                    ? ApiResult<T>.Success(default!)
                    : ApiResult<T>.Success((await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false))!);
            }

            return ApiResult<T>.Failure(await BrightTimeApi.ProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Failure(ApiProblem.Offline);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Failure(new ApiProblem(ApiProblem.UnreadableType, null, 0));
        }
    }
}
