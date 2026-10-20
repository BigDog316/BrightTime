using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrightTime.Modules.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The FCM sender (D40) against a stand-in for Google: the service account's signed assertion,
/// one access token for many sends, the message FCM is given, and what each answer means.
/// </summary>
public sealed class FcmPushSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        public HttpStatusCode SendStatus { get; set; } = HttpStatusCode.OK;

        public string SendBody { get; set; } = """{"name":"projects/kettle/messages/1"}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request, body));
            return request.RequestUri!.Host == "oauth2.googleapis.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"ya29.test","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(SendStatus) { Content = new StringContent(SendBody, Encoding.UTF8, "application/json") };
        }
    }

    private static (FcmPushSender Sender, FakeGoogle Google, RSA Key, HttpClient Client) Sender()
    {
        RSA key = RSA.Create(2048);
        string account = JsonSerializer.Serialize(new
        {
            type = "service_account",
            client_email = "push@kettle.iam.gserviceaccount.com",
            private_key = key.ExportPkcs8PrivateKeyPem(),
            token_uri = "https://oauth2.googleapis.com/token",
        });
        FakeGoogle google = new();
        HttpClient client = new(google);
        FcmPushSender sender = new(client, new FcmOptions { ProjectId = "kettle", ServiceAccountJson = account }, TimeProvider.System, NullLogger<FcmPushSender>.Instance);
        return (sender, google, key, client);
    }

    private static PushMessage Message(string token = "device-token") =>
        new("android", token, "session_reminder", "Starting soon", "Robotics starts on Tue 21 Oct, 4:00 PM.", "/listings/robotics");

    [Fact]
    public async Task One_signed_assertion_buys_a_token_for_many_sends_and_the_message_carries_only_the_words_and_a_path()
    {
        (FcmPushSender sender, FakeGoogle google, RSA key, HttpClient client) = Sender();
        using (client)
        using (sender)
        using (key)
        {
            (await sender.SendAsync(Message(), Ct)).ShouldBe(PushOutcome.Sent);
            (await sender.SendAsync(Message("another"), Ct)).ShouldBe(PushOutcome.Sent);

            google.Seen.Count(s => s.Request.RequestUri!.Host == "oauth2.googleapis.com").ShouldBe(1);

            // The assertion: the service account, FCM's scope, Google's token endpoint, signed with its key.
            string assertion = Uri.UnescapeDataString(google.Seen[0].Body.Split('&').Single(p => p.StartsWith("assertion=", StringComparison.Ordinal))["assertion=".Length..]);
            string[] parts = assertion.Split('.');
            key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();
            using JsonDocument claims = JsonDocument.Parse(Decode(parts[1]));
            claims.RootElement.GetProperty("iss").GetString().ShouldBe("push@kettle.iam.gserviceaccount.com");
            claims.RootElement.GetProperty("scope").GetString().ShouldBe(FcmPushSender.Scope);
            claims.RootElement.GetProperty("aud").GetString().ShouldBe("https://oauth2.googleapis.com/token");

            (HttpRequestMessage send, string body) = google.Seen[1];
            send.RequestUri!.AbsoluteUri.ShouldBe("https://fcm.googleapis.com/v1/projects/kettle/messages:send");
            send.Headers.Authorization!.Parameter.ShouldBe("ya29.test");
            using JsonDocument message = JsonDocument.Parse(body);
            JsonElement m = message.RootElement.GetProperty("message");
            m.GetProperty("token").GetString().ShouldBe("device-token");
            m.GetProperty("notification").GetProperty("title").GetString().ShouldBe("Starting soon");
            m.GetProperty("data").GetProperty("deepLink").GetString().ShouldBe("/listings/robotics");
        }
    }

    [Fact]
    public async Task A_token_fcm_has_forgotten_is_unregistered_and_other_refusals_are_failures()
    {
        (FcmPushSender sender, FakeGoogle google, RSA key, HttpClient client) = Sender();
        using (client)
        using (sender)
        using (key)
        {
            google.SendStatus = HttpStatusCode.NotFound;
            google.SendBody = """{"error":{"code":404,"status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}""";
            (await sender.SendAsync(Message(), Ct)).ShouldBe(PushOutcome.Unregistered);

            google.SendStatus = HttpStatusCode.InternalServerError;
            google.SendBody = """{"error":{"code":500,"status":"INTERNAL"}}""";
            (await sender.SendAsync(Message(), Ct)).ShouldBe(PushOutcome.Failed);
        }
    }

    private static byte[] Decode(string base64Url)
    {
        string padded = base64Url.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
