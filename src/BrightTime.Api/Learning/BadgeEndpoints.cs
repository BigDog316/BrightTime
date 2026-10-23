using System.Security.Claims;
using System.Text.Json.Nodes;
using BrightTime.Hosting.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Badges;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Learning;

public sealed record AwardRequest(Guid EnrollmentId, string? Narrative);

public sealed record RevokeRequest(string? Reason);

/// <summary>
/// Open Badges (M4, D66): an organisation's badges, awarded and revoked by its people; the badges a
/// young person holds; and, for anyone, the documents a verifier fetches: the issuer's DID
/// document, its revocation list, each achievement, and each credential.
/// </summary>
public static class BadgeEndpoints
{
    private const string LinkedData = "application/ld+json";

    public static RouteGroupBuilder MapBadgeEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/badge-classes", ListAsync).Produces<IReadOnlyList<BadgeClassView>>();
        org.MapPost("/badge-classes", CreateAsync).Produces<BadgeClassView>(StatusCodes.Status201Created);
        org.MapPut("/badge-classes/{id:guid}", UpdateAsync).Produces<BadgeClassView>();
        org.MapPost("/badge-classes/{id:guid}/publish", PublishAsync).Produces<BadgeClassView>();
        org.MapPost("/badge-classes/{id:guid}/retire", RetireAsync).Produces<BadgeClassView>();
        org.MapGet("/badge-classes/{id:guid}/awards", AwardsAsync).WithMetadata(new YouthRecordAttribute(Badges.Read)).Produces<IReadOnlyList<AwardView>>();
        org.MapPost("/badge-classes/{id:guid}/awards", IssueAsync).WithMetadata(new YouthRecordAttribute(Badges.Issued)).Produces<AwardView>(StatusCodes.Status201Created);
        org.MapPost("/badge-assertions/{id:guid}/revoke", RevokeAsync).WithMetadata(new YouthRecordAttribute(Badges.Revoked)).Produces<AwardView>();

        api.MapGet("/me/badges", HeldAsync).RequireAuthorization().WithMetadata(new YouthRecordAttribute(Badges.Read)).Produces<IReadOnlyList<HeldBadge>>();

        // What a verifier fetches. None of it names the young person: the credential carries only a salted hash.
        RouteGroupBuilder open = api.MapGroup("/public").AllowAnonymous().RequireRateLimiting(Public.PublicEndpoints.RateLimit);
        open.MapGet("/issuers/{orgId:guid}/did.json", DidDocumentAsync);
        open.MapGet("/issuers/{orgId:guid}/ob2", Issuer2Async);
        open.MapGet("/issuers/{orgId:guid}/revocations", RevocationsAsync);
        open.MapGet("/badges/classes/{id:guid}", AchievementAsync);
        open.MapGet("/badges/classes/{id:guid}/ob2", BadgeClass2Async);
        open.MapGet("/badges/classes/{id:guid}/image.svg", ImageAsync);
        open.MapGet("/badges/{slug}", VerifyAsync).Produces<Verification>();
        open.MapGet("/badges/{slug}/ob2", Assertion2Async);
        return api;
    }

    // ---- the organisation ----------------------------------------------------------------------

    private static async Task<IResult> ListAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, Badges badges, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Badges.Issue, OrganisationRoles.Staff)
            ?? Results.Ok(await badges.ListAsync(new OrganisationId(orgId), ct));

    private static async Task<IResult> CreateAsync(Guid orgId, BadgeClassInput body, ClaimsPrincipal user, IAuthorizationService authorization, SiteContext site, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization, Permissions.Badges.Define, OrganisationRoles.Admin) is { } refused)
        {
            return refused;
        }

        Result<BadgeClassView> made = await badges.CreateAsync(new OrganisationId(orgId), Site(site), body, ct);
        return made.IsSuccess ? Results.Created($"/api/v1/org/{orgId}/badge-classes/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> UpdateAsync(Guid orgId, Guid id, BadgeClassInput body, ClaimsPrincipal user, IAuthorizationService authorization, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await RefusedAsync(user, orgId, authorization, Permissions.Badges.Define, OrganisationRoles.Admin)
            ?? Answer(await badges.UpdateAsync(new OrganisationId(orgId), id, body, ct));
    }

    private static async Task<IResult> PublishAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Badges badges, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Badges.Define, OrganisationRoles.Admin)
            ?? Answer(await badges.PublishAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> RetireAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Badges badges, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Badges.Define, OrganisationRoles.Admin)
            ?? Answer(await badges.RetireAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> AwardsAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Badges badges, CancellationToken ct)
    {
        if (await RefusedAsync(user, orgId, authorization, Permissions.Badges.Issue, OrganisationRoles.Staff) is { } refused)
        {
            return refused;
        }

        Result<IReadOnlyList<AwardView>> awards = await badges.AwardsAsync(new OrganisationId(orgId), id, ct);
        return awards.IsSuccess ? Results.Ok(awards.Value) : ApiProblems.From(awards.Error);
    }

    private static async Task<IResult> IssueAsync(Guid orgId, Guid id, AwardRequest body, ClaimsPrincipal user, IAuthorizationService authorization, SiteContext site, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization, Permissions.Badges.Issue, OrganisationRoles.Staff) is { } refused)
        {
            return refused;
        }

        Result<AwardView> issued = await badges.IssueAsync(new OrganisationId(orgId), Actor(user), Site(site), id, body.EnrollmentId, body.Narrative, ct);
        return issued.IsSuccess ? Results.Created($"/api/v1/public/badges/{issued.Value.VerifySlug}", issued.Value) : ApiProblems.From(issued.Error);
    }

    private static async Task<IResult> RevokeAsync(Guid orgId, Guid id, RevokeRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization, Permissions.Badges.Revoke, OrganisationRoles.Admin) is { } refused)
        {
            return refused;
        }

        Result<AwardView> revoked = await badges.RevokeAsync(new OrganisationId(orgId), id, body.Reason, ct);
        return revoked.IsSuccess ? Results.Ok(revoked.Value) : ApiProblems.From(revoked.Error);
    }

    // ---- the young person ------------------------------------------------------------------------

    private static async Task<IResult> HeldAsync(ClaimsPrincipal user, SiteContext site, Badges badges, CancellationToken ct) =>
        Results.Ok(await badges.HeldAsync(Actor(user), Site(site), ct));

    // ---- anyone ------------------------------------------------------------------------------------

    private static async Task<IResult> DidDocumentAsync(Guid orgId, Badges badges, CancellationToken ct) =>
        Document(await badges.DidDocumentAsync(orgId, ct), "application/did+json");

    private static async Task<IResult> Issuer2Async(Guid orgId, SiteContext site, Badges badges, CancellationToken ct) =>
        Document(await badges.Issuer2Async(orgId, Site(site), ct), LinkedData);

    private static async Task<IResult> RevocationsAsync(Guid orgId, SiteContext site, HttpResponse response, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        // A verifier must see a revocation at once.
        response.Headers.CacheControl = "no-store";
        return Document(await badges.RevocationsAsync(orgId, Site(site), ct), "application/json");
    }

    private static async Task<IResult> AchievementAsync(Guid id, SiteContext site, Badges badges, CancellationToken ct) =>
        Document(await badges.BadgeClassAsync(id, Site(site), openBadges2: false, ct), LinkedData);

    private static async Task<IResult> BadgeClass2Async(Guid id, SiteContext site, Badges badges, CancellationToken ct) =>
        Document(await badges.BadgeClassAsync(id, Site(site), openBadges2: true, ct), LinkedData);

    private static async Task<IResult> ImageAsync(Guid id, Badges badges, CancellationToken ct) =>
        await badges.ImageAsync(id, ct) is { } svg ? Results.Text(svg, "image/svg+xml") : NotFound();

    private static async Task<IResult> VerifyAsync(string slug, HttpResponse response, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return await badges.VerifyAsync(slug, ct) is { } verification ? Results.Ok(verification) : NotFound();
    }

    /// <summary>The OB 2.0 hosted assertion; a revoked one answers 410 Gone with the revocation, as OB 2.0 asks.</summary>
    private static async Task<IResult> Assertion2Async(string slug, SiteContext site, HttpResponse response, Badges badges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.CacheControl = "no-store";
        if (await badges.Assertion2Async(slug, Site(site), ct) is not { } found)
        {
            return NotFound();
        }

        return Results.Text(found.Document.ToJsonString(), LinkedData, statusCode: found.Revoked ? StatusCodes.Status410Gone : StatusCodes.Status200OK);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IResult Document(JsonObject? document, string contentType) =>
        document is null ? NotFound() : Results.Text(document.ToJsonString(), contentType);

    private static IResult NotFound() => ApiProblems.From(Error.NotFound("badges.not_found", "There's no badge like that."));

    private static IResult Answer(Result<BadgeClassView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    /// <summary>A member of the organisation with the badge permission and at least the role named.</summary>
    private static async Task<IResult?> RefusedAsync(ClaimsPrincipal user, Guid orgId, IAuthorizationService authorization, string permission, string role)
    {
        AuthorizationResult result = await authorization.AuthorizeAsync(
            user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) }, new OrganisationScopeRequirement(permission, role));
        return result.Succeeded ? null : ApiProblems.From(Error.Forbidden("badges.forbidden", "Your role in this organisation doesn't cover that."));
    }

    /// <summary>The tenant's own address, which every badge document is built from; never the request's Host.</summary>
    private static Uri Site(SiteContext site) =>
        site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
