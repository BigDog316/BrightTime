using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Records;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Web.Composition;

/// <summary>
/// A young person's project pictures on the website (D104): for an organisation's staff while the
/// project, or a playlist step it's the evidence for, waits for their review; and on a shown
/// portfolio once the project is published. Never at <c>/media</c>, never cached, and anything
/// else, an unknown id included, is a 404.
/// </summary>
public static class ProjectPictureEndpoints
{
    public static string ForReview(Guid orgId, Guid mediaId) => $"/provider/orgs/{orgId:D}/work/pictures/{mediaId:D}";

    public static string ForPortfolio(string slug, Guid projectId, Guid mediaId) =>
        $"/portfolios/{Uri.EscapeDataString(slug)}/projects/{projectId:D}/pictures/{mediaId:D}";

    public static IEndpointRouteBuilder MapProjectPictures(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/provider/orgs/{OrgId:guid}/work/pictures/{MediaId:guid}", ReviewAsync)
            .RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(Projects.Read));
        endpoints.MapGet("/portfolios/{slug}/projects/{id:guid}/pictures/{mediaId:guid}", PublicAsync)
            .WithMetadata(new YouthRecordAttribute(Modules.Learning.Portfolio.Portfolios.PublicRead));
        return endpoints;
    }

    private static async Task<IResult> ReviewAsync(Guid orgId, Guid mediaId, HttpContext http, IAuthorizationService authorization)
    {
        if (http.RequestServices.GetService<Projects>() is not { } projects)
        {
            return Results.NotFound();
        }

        bool reviewsProjects = await CanAsync(http, orgId, Permissions.Projects.Review, authorization).ConfigureAwait(false);
        bool reviewsSteps = await CanAsync(http, orgId, Permissions.Playlists.ReviewSteps, authorization).ConfigureAwait(false);
        if (!reviewsProjects && !reviewsSteps)
        {
            // A plain refusal: the cookie's own forbid would send a picture request to a page.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return Picture(http, await projects.ReviewPictureAsync(new OrganisationId(orgId), mediaId, reviewsProjects, reviewsSteps, http.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> PublicAsync(string slug, Guid id, Guid mediaId, HttpContext http) =>
        http.RequestServices.GetService<Projects>() is { } projects
            ? Picture(http, await projects.PublicPictureAsync(slug, id, mediaId, http.RequestAborted).ConfigureAwait(false), robots: true)
            : Results.NotFound();

    /// <summary>What a portfolio shows is its owner's to take back at any moment, so nothing keeps a copy.</summary>
    private static IResult Picture(HttpContext http, MediaFile? file, bool robots = false)
    {
        if (file is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        if (robots)
        {
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        }

        return Results.File(file.Bytes, file.ContentType);
    }

    private static async Task<bool> CanAsync(HttpContext http, Guid orgId, string permission, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(http.User, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, OrganisationRoles.Staff)).ConfigureAwait(false)).Succeeded;
}
