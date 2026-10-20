using System.Net;
using System.Text;
using BrightTime.Ui.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrightTime.Ui.Shared.Tests;

/// <summary>
/// A stand-in API: answers by method and path from a table, and records what was asked, with
/// which headers. A request it has no answer for is a 404 problem.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Set to make every request fail as if offline.</summary>
    public bool Offline { get; set; }

    public FakeApi On(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes.Insert(0, (method, path, answer));
        return this;
    }

    public FakeApi Json(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, path, _ => Respond(status, json, "application/json"));

    public static HttpResponseMessage Respond(HttpStatusCode status, string? body = null, string mediaType = "application/json") =>
        new(status) { Content = body is null ? null : new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        Respond(status, $$"""{"type":"urn:brighttime:problem:{{code}}","title":"English title","status":{{(int)status}}}""", "application/problem+json");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Offline)
        {
            throw new HttpRequestException("No route to host.");
        }

        string path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
        foreach ((HttpMethod method, string route, Func<HttpRequestMessage, HttpResponseMessage> answer) in _routes)
        {
            if (method == request.Method && string.Equals(route, path, StringComparison.Ordinal))
            {
                return Task.FromResult(answer(request));
            }
        }

        return Task.FromResult(Problem(HttpStatusCode.NotFound, "not-found"));
    }

    /// <summary>The app's services over this fake, as the MAUI host would make them.</summary>
    public ServiceProvider Services(Action<IServiceCollection>? extra = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        extra?.Invoke(services);
        services.AddBrightTimeApp(
            new AppEndpoints(new Uri("https://kettle.test/api/v1/"), new Uri("https://kettle.test/")),
            _ => new HttpClient(this, disposeHandler: false));
        return services.BuildServiceProvider();
    }

    public const string Tenant = """
        {"name":"Kettle County","defaultLocale":"en","timeZone":"America/New_York",
         "locales":[{"code":"en","nativeName":"English","direction":"ltr"},{"code":"es","nativeName":"Español","direction":"ltr"},{"code":"ar","nativeName":"العربية","direction":"rtl"}]}
        """;

    public const string Tokens = """{"accessToken":"access-1","tokenType":"Bearer","expiresIn":900,"refreshToken":"refresh-1","refreshTokenExpiresAt":"2030-01-01T00:00:00Z"}""";

    public const string Renewed = """{"accessToken":"access-2","tokenType":"Bearer","expiresIn":900,"refreshToken":"refresh-2","refreshTokenExpiresAt":"2030-01-01T00:00:00Z"}""";

    public static string Me(string kind) =>
        $$"""{"id":"0b3c8f5e-0000-0000-0000-000000000001","userName":"sam@example.net","accountKind":"{{kind}}","locale":null,"roles":[],"permissions":[],"organisations":[]}""";

    public const string Capabilities = """
        {"isUnder13":false,"hasAccountConsent":true,"canSelfEnroll":true,"canPostInCommunity":false,"canUploadMedia":false,
         "canPublishShowcase":true,"canReceiveSms":false,"canBeMessagedByOrganisation":false}
        """;

    public const string Results = """
        {"items":[{"id":"5e1d0000-0000-0000-0000-000000000001","slug":"robotics-club","kind":"program","title":"Robotics Club",
                   "summary":"Build and code robots.","organisation":{"id":"5e1d0000-0000-0000-0000-0000000000aa","slug":"kettle-library","name":"Kettle Library"},
                   "cost":{"isFree":true,"fromCents":null},"nextStartAt":"2026-10-21T20:00:00Z","nextDeadlineAt":null,"distanceMetres":null,
                   "why":[{"key":"finder.reason.starts_in_days","args":{"days":2}},{"key":"no.such.key","args":{}}]}],
         "nextCursor":null,"notices":[]}
        """;
}
