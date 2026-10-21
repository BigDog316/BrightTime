using System.Security.Claims;
using BrightTime.Modules.Enrollment.Forms;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Forms;

public sealed record NewFormRequest(string? Name, IReadOnlyList<FormQuestion>? Questions);

public sealed record FormQuestionsRequest(IReadOnlyList<FormQuestion>? Questions);

public sealed record ListingFormRequest(Guid FormId);

/// <summary>
/// An organisation's registration forms (M3a, D45): drafts, published versions, and the form each
/// of its listings asks for. Forms collect what families tell a provider, sensitive answers among
/// them, so only an organisation's owners and admins write them.
/// </summary>
public static class FormEndpoints
{
    public static RouteGroupBuilder MapFormEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/forms", ListAsync);
        org.MapPost("/forms", CreateAsync);
        org.MapGet("/forms/{id:guid}", GetAsync);
        org.MapPut("/forms/{id:guid}", UpdateAsync);
        org.MapPost("/forms/{id:guid}/publish", PublishAsync);
        org.MapPost("/forms/{id:guid}/revise", ReviseAsync);
        org.MapPost("/forms/{id:guid}/retire", RetireAsync);
        org.MapPut("/listings/{listingId:guid}/form", AttachAsync);
        org.MapDelete("/listings/{listingId:guid}/form", DetachAsync);
        return api;
    }

    private static async Task<IResult> ListAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization) ?? Results.Ok(await forms.ListAsync(new OrganisationId(orgId), ct));

    private static async Task<IResult> GetAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization) ?? Answer(await forms.GetAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> CreateAsync(Guid orgId, NewFormRequest body, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<FormView> made = await forms.CreateAsync(new OrganisationId(orgId), Actor(user), body.Name ?? string.Empty, new FormSchema(body.Questions ?? []), ct);
        return made.IsSuccess ? Results.Created($"/api/v1/org/{orgId}/forms/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> UpdateAsync(Guid orgId, Guid id, FormQuestionsRequest body, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await RefusedAsync(user, orgId, authorization)
            ?? Answer(await forms.UpdateAsync(new OrganisationId(orgId), id, new FormSchema(body.Questions ?? []), ct));
    }

    private static async Task<IResult> PublishAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization) ?? Answer(await forms.PublishAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> ReviseAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct)
    {
        if (await RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<FormView> next = await forms.ReviseAsync(new OrganisationId(orgId), id, Actor(user), ct);
        return next.IsSuccess ? Results.Created($"/api/v1/org/{orgId}/forms/{next.Value.Id}", next.Value) : ApiProblems.From(next.Error);
    }

    private static async Task<IResult> RetireAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization) ?? Answer(await forms.RetireAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> AttachAsync(Guid orgId, Guid listingId, ListingFormRequest body, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result attached = await forms.AttachAsync(new OrganisationId(orgId), listingId, body.FormId, ct);
        return attached.IsSuccess ? Results.NoContent() : ApiProblems.From(attached.Error);
    }

    private static async Task<IResult> DetachAsync(Guid orgId, Guid listingId, ClaimsPrincipal user, IAuthorizationService authorization, FormDefinitions forms, CancellationToken ct)
    {
        if (await RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result detached = await forms.DetachAsync(new OrganisationId(orgId), listingId, ct);
        return detached.IsSuccess ? Results.NoContent() : ApiProblems.From(detached.Error);
    }

    private static IResult Answer(Result<FormView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    /// <summary>An owner or admin of the organisation, able to edit it.</summary>
    private static async Task<IResult?> RefusedAsync(ClaimsPrincipal user, Guid orgId, IAuthorizationService authorization)
    {
        AuthorizationResult result = await authorization.AuthorizeAsync(
            user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) }, new OrganisationScopeRequirement(Permissions.Organisations.Edit, OrganisationRoles.Admin));
        return result.Succeeded ? null : ApiProblems.From(Error.Forbidden("form.forbidden", "Only an owner or admin of this organisation manages its forms."));
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
