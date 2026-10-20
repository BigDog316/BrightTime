using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Notifications;

/// <summary>Where Firebase Cloud Messaging is (<c>Push:Fcm:*</c>). Unset, pushes are only logged.</summary>
public sealed class FcmOptions
{
    public const string SectionName = "Push:Fcm";

    /// <summary>The Firebase project's id.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// The service account's key file, as JSON (<c>client_email</c>, <c>private_key</c>,
    /// <c>token_uri</c>). A secret: from the deployment's secret store, never from a committed file.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(ServiceAccountJson);
}

/// <summary>
/// Pushes through Firebase Cloud Messaging's HTTP v1 API (D40): Android directly, iOS through APNs
/// when the app registers with Firebase there. Each send is one device's token, already worded in
/// its language (<see cref="NotificationText"/>).
/// </summary>
/// <remarks>
/// <para>
/// FCM wants an OAuth access token, minted here from the service account: a JWT signed with its
/// key (RS256), exchanged at Google's token endpoint, and kept until shortly before it runs out.
/// </para>
/// <para>
/// A token FCM no longer knows (<c>UNREGISTERED</c>: the app was removed, or the token was
/// replaced) comes back as <see cref="PushOutcome.Unregistered"/>, so the device can be
/// forgotten. The message carries the words and an in-app path; nothing personal beyond that.
/// </para>
/// </remarks>
public sealed partial class FcmPushSender(HttpClient http, FcmOptions options, TimeProvider time, ILogger<FcmPushSender> logger) : IPushSender, IDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly ServiceAccount _account = ServiceAccount.Parse(options.ServiceAccountJson);
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;

    public async Task<PushOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? token = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return PushOutcome.Failed;
        }

        Dictionary<string, string> data = new(StringComparer.Ordinal) { ["kind"] = message.Kind };
        if (message.DeepLink is { } link)
        {
            data["deepLink"] = link;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(options.ProjectId!)}/messages:send"))
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    token = message.Token,
                    notification = new { title = message.Title, body = message.Body },
                    data,
                    android = new { priority = "HIGH" },
                    apns = new { payload = new { aps = new { sound = "default" } } },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Sent;
            }

            // A token FCM has forgotten: 404 with the error code UNREGISTERED.
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound || body.Contains("\"UNREGISTERED\"", StringComparison.Ordinal))
            {
                return PushOutcome.Unregistered;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _accessToken = null;
            }

            LogRefused(logger, (int)response.StatusCode, message.Platform);
            return PushOutcome.Failed;
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return PushOutcome.Failed;
        }
    }

    public void Dispose() => _tokenGate.Dispose();

    private async Task<string?> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { } cached && time.GetUtcNow() < _accessExpiresAt - RenewBefore)
        {
            return cached;
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } again && time.GetUtcNow() < _accessExpiresAt - RenewBefore)
            {
                return again;
            }

            DateTimeOffset now = time.GetUtcNow();
            using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = Assertion(_account, now),
            });
            using HttpResponseMessage response = await http.PostAsync(_account.TokenUri, form, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogTokenRefused(logger, (int)response.StatusCode);
                return null;
            }

            TokenResponse? issued = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
            if (issued?.AccessToken is not { Length: > 0 } accessToken)
            {
                return null;
            }

            _accessToken = accessToken;
            _accessExpiresAt = now.AddSeconds(issued.ExpiresIn > 0 ? issued.ExpiresIn : 3600);
            return accessToken;
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>The signed JWT Google's token endpoint exchanges for an access token (RFC 7523).</summary>
    internal static string Assertion(ServiceAccount account, DateTimeOffset now)
    {
        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        string claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.ClientEmail,
            scope = Scope,
            aud = account.TokenUri.AbsoluteUri,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));
        string unsigned = header + "." + claims;
        using RSA key = RSA.Create();
        key.ImportFromPem(account.PrivateKey);
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The fields of a service account key file this sender needs.</summary>
    internal sealed record ServiceAccount(string ClientEmail, string PrivateKey, Uri TokenUri)
    {
        public static ServiceAccount Parse(string? json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string Field(string name) => root.TryGetProperty(name, out JsonElement value) && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException($"Push:Fcm:ServiceAccountJson has no {name}.");
            return new ServiceAccount(
                Field("client_email"),
                Field("private_key"),
                new Uri(root.TryGetProperty("token_uri", out JsonElement uri) && uri.GetString() is { Length: > 0 } u ? u : "https://oauth2.googleapis.com/token"));
        }

        /// <summary>Never the key.</summary>
        public override string ToString() => $"ServiceAccount {{ ClientEmail = {ClientEmail} }}";
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "FCM refused a push to a {Platform} device with status {Status}.")]
    private static partial void LogRefused(ILogger logger, int status, string platform);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Error, Message = "Google's token endpoint refused the service account with status {Status}.")]
    private static partial void LogTokenRefused(ILogger logger, int status);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "FCM could not be reached: {Exception}.")]
    private static partial void LogUnreachable(ILogger logger, string exception);
}
