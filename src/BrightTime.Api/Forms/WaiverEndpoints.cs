using System.Security.Claims;
using BrightTime.Modules.Enrollment.Waivers;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Forms;

public sealed record NewWaiverRequest(string? Title, string? Body, bool RequiresGuardian = true);

public sealed record WaiverTextRequest(string? Body, bool RequiresGuardian = true);

public sealed record ListingWaiverRequest(bool IsRequired = true);

/// <summary>
/// An organisation's waivers (M3a, D47), managed by its owners and admins as its forms are:
/// drafted, published (fixed from then on), revised into new versions, and attached to listings.
/// </summary>
public static class WaiverEndpoints
{
    public static RouteGroupBuilder MapWaiverEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/waivers", ListAsync);
        org.MapPost("/waivers", CreateAsync);
        org.MapGet("/waivers/{id:guid}", GetAsync);
        org.MapPut("/waivers/{id:guid}", UpdateAsync);
        org.MapPost("/waivers/{id:guid}/publish", PublishAsync);
        org.MapPost("/waivers/{id:guid}/revise", ReviseAsync);
        org.MapPost("/waivers/{id:guid}/retire", RetireAsync);
        org.MapPut("/listings/{listingId:guid}/waivers/{waiverId:guid}", AttachAsync);
        org.MapDelete("/listings/{listingId:guid}/waivers/{waiverId:guid}", DetachAsync);
        return api;
    }

    private static async Task<IResult> ListAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Results.Ok(await waivers.ListAsync(new OrganisationId(orgId), ct));

    private static async Task<IResult> GetAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Answer(await waivers.GetAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> CreateAsync(Guid orgId, NewWaiverRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await FormEndpoints.RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<WaiverView> made = await waivers.CreateAsync(new OrganisationId(orgId), body.Title ?? string.Empty, body.Body ?? string.Empty, body.RequiresGuardian, ct);
        return made.IsSuccess ? Results.Created($"/api/v1/org/{orgId}/waivers/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> UpdateAsync(Guid orgId, Guid id, WaiverTextRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await FormEndpoints.RefusedAsync(user, orgId, authorization)
            ?? Answer(await waivers.UpdateAsync(new OrganisationId(orgId), id, body.Body ?? string.Empty, body.RequiresGuardian, ct));
    }

    private static async Task<IResult> PublishAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Answer(await waivers.PublishAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> ReviseAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct)
    {
        if (await FormEndpoints.RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<WaiverView> next = await waivers.ReviseAsync(new OrganisationId(orgId), id, ct);
        return next.IsSuccess ? Results.Created($"/api/v1/org/{orgId}/waivers/{next.Value.Id}", next.Value) : ApiProblems.From(next.Error);
    }

    private static async Task<IResult> RetireAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Answer(await waivers.RetireAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> AttachAsync(Guid orgId, Guid listingId, Guid waiverId, ListingWaiverRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await FormEndpoints.RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result attached = await waivers.AttachAsync(new OrganisationId(orgId), listingId, waiverId, body.IsRequired, ct);
        return attached.IsSuccess ? Results.NoContent() : ApiProblems.From(attached.Error);
    }

    private static async Task<IResult> DetachAsync(Guid orgId, Guid listingId, Guid waiverId, ClaimsPrincipal user, IAuthorizationService authorization, Waivers waivers, CancellationToken ct)
    {
        if (await FormEndpoints.RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result detached = await waivers.DetachAsync(new OrganisationId(orgId), listingId, waiverId, ct);
        return detached.IsSuccess ? Results.NoContent() : ApiProblems.From(detached.Error);
    }

    private static IResult Answer(Result<WaiverView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);
}
