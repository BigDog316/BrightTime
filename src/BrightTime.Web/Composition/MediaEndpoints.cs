using BrightTime.Modules.Platform.Contracts;
using Microsoft.Net.Http.Headers;

namespace BrightTime.Web.Composition;

/// <summary>
/// Public media (D103): a clean, public media object's bytes by its id, for the pictures on
/// collections and neighbourhood pages. Anything else, an unknown id included, is a 404, so the
/// endpoint says nothing about what exists.
/// </summary>
/// <remarks>
/// A media object's bytes never change (a new picture is a new object), so a copy is good for a
/// year and the hash of the bytes is its tag. Served with its own type and <c>nosniff</c>, and
/// as a download-proof inline image: nothing in it can run as a page.
/// </remarks>
public static class MediaEndpoints
{
    public const string Path = "/media/{id:guid}";

    public static string For(Guid mediaId) => "/media/" + mediaId.ToString("D");

    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Path, async (Guid id, HttpContext http, CancellationToken ct) =>
        {
            // Without a database (the sample site) there's no media at all.
            if (http.RequestServices.GetService<IMediaLibrary>() is not { } media
                || await media.GetPublicAsync(id, ct).ConfigureAwait(false) is not { } file)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.File(file.Bytes, file.ContentType, entityTag: new EntityTagHeaderValue(file.ETag));
        });
        return endpoints;
    }
}
