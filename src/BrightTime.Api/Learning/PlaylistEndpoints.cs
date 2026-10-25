using System.Security.Claims;
using BrightTime.Hosting.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Playlists;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Learning;

public sealed record PlaylistStepsRequest(IReadOnlyList<StepInput>? Steps);

public sealed record StepReviewRequest(Guid EnrollmentId, bool Approve, string? Note);

public sealed record StepDoneRequest(string? Reflection);

/// <summary>
/// Playlists (M4, D67): an organisation's playlists, written as drafts and published; the steps
/// young people submit, reviewed by its staff; the playlists anyone can read; and the young
/// person's own, started and worked through, with a badge at the end.
/// </summary>
public static class PlaylistEndpoints
{
    public static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}/playlists").RequireAuthorization();
        org.MapGet("/", ListAsync).Produces<IReadOnlyList<PlaylistView>>();
        org.MapPost("/", CreateAsync).Produces<PlaylistView>(StatusCodes.Status201Created);
        org.MapGet("/{id:guid}", GetAsync).Produces<PlaylistView>();
        org.MapPut("/{id:guid}", UpdateAsync).Produces<PlaylistView>();
        org.MapPut("/{id:guid}/steps", StepsAsync).Produces<PlaylistView>();
        org.MapPost("/{id:guid}/publish", PublishAsync).Produces<PlaylistView>();
        org.MapPost("/{id:guid}/archive", ArchiveAsync).Produces<PlaylistView>();
        org.MapGet("/{id:guid}/submissions", SubmissionsAsync).WithMetadata(new YouthRecordAttribute(Playlists.Read)).Produces<IReadOnlyList<StepSubmission>>();
        org.MapPost("/{id:guid}/steps/{stepId:guid}/review", ReviewAsync).WithMetadata(new YouthRecordAttribute(Playlists.Reviewed));

        api.MapGet("/me/playlists", MineAsync).RequireAuthorization().WithMetadata(new YouthRecordAttribute(Playlists.Read)).Produces<IReadOnlyList<MyPlaylist>>();
        api.MapPost("/playlists/{id:guid}/enroll", EnrollAsync).RequireAuthorization().WithMetadata(new YouthRecordAttribute(Playlists.Progressed)).Produces<MyPlaylist>();
        api.MapPut("/me/playlists/{id:guid}/steps/{stepId:guid}", DoStepAsync).RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(Playlists.Progressed)).Produces<MyPlaylist>();

        RouteGroupBuilder open = api.MapGroup("/public/playlists").AllowAnonymous().RequireRateLimiting(Public.PublicEndpoints.RateLimit);
        open.MapGet("/", PublishedAsync).Produces<IReadOnlyList<PublicPlaylist>>();
        open.MapGet("/{slug}", OneAsync).Produces<PublicPlaylist>();
        return api;
    }

    // ---- the organisation ----------------------------------------------------------------------

    private static async Task<IResult> ListAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author) ?? Results.Ok(await playlists.ListAsync(new OrganisationId(orgId), ct));

    private static async Task<IResult> GetAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author) ?? Answer(await playlists.GetAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> CreateAsync(Guid orgId, PlaylistInput body, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author) is { } refused)
        {
            return refused;
        }

        Result<PlaylistView> made = await playlists.CreateAsync(new OrganisationId(orgId), Actor(user), body, ct);
        return made.IsSuccess ? Results.Created($"/api/v1/org/{orgId}/playlists/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> UpdateAsync(Guid orgId, Guid id, PlaylistInput body, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author)
            ?? Answer(await playlists.UpdateAsync(new OrganisationId(orgId), id, body, ct));
    }

    private static async Task<IResult> StepsAsync(Guid orgId, Guid id, PlaylistStepsRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author)
            ?? Answer(await playlists.SetStepsAsync(new OrganisationId(orgId), id, body.Steps ?? [], ct));
    }

    private static async Task<IResult> PublishAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author) ?? Answer(await playlists.PublishAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> ArchiveAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Playlists.Author) ?? Answer(await playlists.ArchiveAsync(new OrganisationId(orgId), id, ct));

    private static async Task<IResult> SubmissionsAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Playlists playlists, CancellationToken ct)
    {
        if (await RefusedAsync(user, orgId, authorization, Permissions.Playlists.ReviewSteps) is { } refused)
        {
            return refused;
        }

        Result<IReadOnlyList<StepSubmission>> waiting = await playlists.SubmissionsAsync(new OrganisationId(orgId), id, ct);
        return waiting.IsSuccess ? Results.Ok(waiting.Value) : ApiProblems.From(waiting.Error);
    }

    private static async Task<IResult> ReviewAsync(
        Guid orgId, Guid id, Guid stepId, StepReviewRequest body, ClaimsPrincipal user, IAuthorizationService authorization, SiteContext site, Playlists playlists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await RefusedAsync(user, orgId, authorization, Permissions.Playlists.ReviewSteps) is { } refused)
        {
            return refused;
        }

        Result reviewed = await playlists.ReviewAsync(new OrganisationId(orgId), Actor(user), Site(site), id, stepId, body.EnrollmentId, body.Approve, body.Note, ct);
        return reviewed.IsSuccess ? Results.NoContent() : ApiProblems.From(reviewed.Error);
    }

    // ---- the young person ------------------------------------------------------------------------

    private static async Task<IResult> MineAsync(ClaimsPrincipal user, Playlists playlists, CancellationToken ct) =>
        Results.Ok(await playlists.MineAsync(Actor(user), ct));

    private static async Task<IResult> EnrollAsync(Guid id, ClaimsPrincipal user, Playlists playlists, CancellationToken ct)
    {
        Result<MyPlaylist> started = await playlists.EnrollAsync(Actor(user), id, ct);
        return started.IsSuccess ? Results.Ok(started.Value) : ApiProblems.From(started.Error);
    }

    private static async Task<IResult> DoStepAsync(Guid id, Guid stepId, StepDoneRequest body, ClaimsPrincipal user, SiteContext site, Playlists playlists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<MyPlaylist> done = await playlists.DoStepAsync(Actor(user), Site(site), id, stepId, body.Reflection, ct);
        return done.IsSuccess ? Results.Ok(done.Value) : ApiProblems.From(done.Error);
    }

    // ---- anyone ------------------------------------------------------------------------------------

    private static async Task<IResult> PublishedAsync(Playlists playlists, CancellationToken ct) => Results.Ok(await playlists.PublishedAsync(ct));

    private static async Task<IResult> OneAsync(string slug, Playlists playlists, CancellationToken ct) =>
        await playlists.PublishedAsync(slug, ct) is { } playlist
            ? Results.Ok(playlist)
            : ApiProblems.From(Error.NotFound("playlists.not_found", "There's no playlist like that."));

    // ---- helpers ---------------------------------------------------------------------------------

    private static IResult Answer(Result<PlaylistView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    /// <summary>A member of the organisation, staff or above, with the playlist permission named.</summary>
    private static async Task<IResult?> RefusedAsync(ClaimsPrincipal user, Guid orgId, IAuthorizationService authorization, string permission)
    {
        AuthorizationResult result = await authorization.AuthorizeAsync(
            user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) }, new OrganisationScopeRequirement(permission, OrganisationRoles.Staff));
        return result.Succeeded ? null : ApiProblems.From(Error.Forbidden("playlists.forbidden", "Your role in this organisation doesn't cover that."));
    }

    private static Uri Site(SiteContext site) =>
        site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
