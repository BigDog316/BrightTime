using System.Globalization;
using BrightTime.Modules.Platform.Contracts;
using Microsoft.Net.Http.Headers;

namespace BrightTime.Web.Composition;

/// <summary>
/// Public media (D103): a clean, public media object's bytes by its id, for the pictures on
/// collections and neighbourhood pages. Anything else, an unknown id included, is a 404, so the
/// endpoint says nothing about what exists.
/// </summary>
/// <remarks>
/// <para>
/// A media object's bytes never change (a new picture is a new object), so a copy is good for a
/// year and the hash of the bytes is its tag. Served with its own type and <c>nosniff</c>, and
/// as a download-proof inline image: nothing in it can run as a page.
/// </para>
/// <para>
/// <c>/media/{id}/480</c> and <c>/media/{id}/1280</c> are its smaller copies (D106), served the
/// same way. Where there's none, the original stands in, by a redirect: one kept a year when the
/// image isn't wider than that, and one not kept at all while the jobs worker hasn't made it yet.
/// </para>
/// </remarks>
public static class MediaEndpoints
{
    public const string Path = "/media/{id:guid}";

    public const string SizedPath = "/media/{id:guid}/{size:int}";

    public static string For(Guid mediaId) => "/media/" + mediaId.ToString("D");

    /// <summary>The image's copy at <paramref name="size"/>, one of <see cref="MediaSizes.All"/>.</summary>
    public static string For(Guid mediaId, int size) =>
        MediaSizes.IsSize(size) ? For(mediaId) + "/" + size.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(size), size, "Not one of MediaSizes.All.");

    /// <summary>The copy for a card or a list: one size, as cards are small.</summary>
    public static string Card(Guid mediaId) => For(mediaId, MediaSizes.Card);

    /// <summary>The copy for a page's main picture, for a browser that takes no <c>srcset</c>.</summary>
    public static string Page(Guid mediaId) => For(mediaId, MediaSizes.Page);

    /// <summary>A page's main picture is as wide as the page's column (44rem) or the screen.</summary>
    public const string PageWidth = "(min-width: 46rem) 44rem, 100vw";

    /// <summary>A listing's other pictures, three or so to the column's width, two on a phone.</summary>
    public const string GalleryWidth = "(min-width: 46rem) 15rem, 50vw";

    /// <summary>A <c>srcset</c> of the image's copies, by width, for a browser to choose from.</summary>
    public static string SrcSet(Guid mediaId) =>
        string.Join(", ", MediaSizes.All.Select(s => For(mediaId, s) + " " + s.ToString(CultureInfo.InvariantCulture) + "w"));

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

            return Send(http, file);
        });
        endpoints.MapGet(SizedPath, async (Guid id, int size, HttpContext http, CancellationToken ct) =>
        {
            if (!MediaSizes.IsSize(size)
                || http.RequestServices.GetService<IMediaLibrary>() is not { } media
                || await media.GetPublicSizeAsync(id, size, ct).ConfigureAwait(false) is not { } sized)
            {
                return Results.NotFound();
            }

            if (sized.File is { } file)
            {
                return Send(http, file);
            }

            http.Response.Headers.CacheControl = sized.Settled ? "public, max-age=31536000, immutable" : "no-store";
            return Results.Redirect(For(id));
        });
        return endpoints;
    }

    private static IResult Send(HttpContext http, MediaFile file)
    {
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.File(file.Bytes, file.ContentType, entityTag: new EntityTagHeaderValue(file.ETag));
    }
}
