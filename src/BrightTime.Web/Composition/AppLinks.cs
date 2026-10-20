using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace BrightTime.Web.Composition;

/// <summary>
/// Which mobile apps may open this site's links (<c>AppLinks:*</c>). Empty until the apps are
/// signed: the files answer 404 then, and every link opens the web page.
/// </summary>
public sealed class AppLinksOptions
{
    public const string SectionName = "AppLinks";

    /// <summary>The iOS app's identifiers, <c>TEAMID.bundle.id</c>.</summary>
    public IList<string> AppleAppIds { get; } = [];

    /// <summary>The Android app's package name.</summary>
    public string? AndroidPackage { get; set; }

    /// <summary>The SHA-256 fingerprints of the Android app's signing certificates, colon-separated hex.</summary>
    public IList<string> AndroidCertificateFingerprints { get; } = [];
}

/// <summary>
/// The files that let the app claim this site's links (D35): <c>apple-app-site-association</c>
/// for universal links on iOS, <c>assetlinks.json</c> for verified App Links on Android. Every
/// tenant's address serves the same app.
/// </summary>
/// <remarks>
/// Only <c>/app/*</c> is claimed: the paths written for the app, such as a consent email's link.
/// Anything else, a claim or reset link included, stays on the web, where the page works without
/// the app. The same entries share passwords and passkeys with the app (<c>webcredentials</c>,
/// <c>get_login_creds</c>), so an account set up on one signs in on the other.
/// </remarks>
public static class AppLinks
{
    public const string ApplePath = "/.well-known/apple-app-site-association";
    public const string AndroidPath = "/.well-known/assetlinks.json";
    public const string ClaimedPaths = "/app/*";

    public static IEndpointRouteBuilder MapAppLinks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(ApplePath, (IOptions<AppLinksOptions> options, HttpContext http) =>
            Answer(http, Apple(options.Value))).AllowAnonymous();
        endpoints.MapGet(AndroidPath, (IOptions<AppLinksOptions> options, HttpContext http) =>
            Answer(http, Android(options.Value))).AllowAnonymous();
        return endpoints;
    }

    /// <summary>The iOS file, or null when no app is configured.</summary>
    public static JsonNode? Apple(AppLinksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AppleAppIds.Count == 0)
        {
            return null;
        }

        JsonArray ids = [.. options.AppleAppIds.Select(id => (JsonNode?)JsonValue.Create(id))];
        return new JsonObject
        {
            ["applinks"] = new JsonObject
            {
                ["details"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["appIDs"] = ids,
                        ["components"] = new JsonArray { new JsonObject { ["/"] = ClaimedPaths } },
                    },
                },
            },
            ["webcredentials"] = new JsonObject { ["apps"] = ids.DeepClone() },
        };
    }

    /// <summary>The Android file, or null when no app is configured.</summary>
    public static JsonNode? Android(AppLinksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.AndroidPackage) || options.AndroidCertificateFingerprints.Count == 0)
        {
            return null;
        }

        return new JsonArray
        {
            new JsonObject
            {
                ["relation"] = new JsonArray { "delegate_permission/common.handle_all_urls", "delegate_permission/common.get_login_creds" },
                ["target"] = new JsonObject
                {
                    ["namespace"] = "android_app",
                    ["package_name"] = options.AndroidPackage,
                    ["sha256_cert_fingerprints"] = new JsonArray([.. options.AndroidCertificateFingerprints.Select(f => (JsonNode?)JsonValue.Create(f))]),
                },
            },
        };
    }

    // Served as JSON with no redirect, as both platforms require, and cached for an hour: the
    // platforms fetch them at install and now and then after.
    private static IResult Answer(HttpContext http, JsonNode? file)
    {
        if (file is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Text(file.ToJsonString(JsonOptions), "application/json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
