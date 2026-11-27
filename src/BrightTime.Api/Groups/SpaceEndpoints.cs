using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Community.Groups;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Groups;

/// <summary>A post, or a reply to one published in the same space.</summary>
public sealed record PostRequest(string? Body, Guid? ReplyTo);

/// <summary>
/// Spaces (M5, D120), as their members use them from the app: the spaces they're in, one with its
/// posts, a post or a reply, and taking down their own. Opening a space and choosing its staff are
/// the console's. Membership is the scope: nobody reads or writes a space they aren't in, and the
/// not-found answer doesn't say whether it exists.
/// </summary>
public static class SpaceEndpoints
{
    public static RouteGroupBuilder MapSpaceEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder spaces = api.MapGroup("/spaces").RequireAuthorization();
        spaces.MapGet("/", ListAsync).WithMetadata(new YouthRecordAttribute(Spaces.Read)).Produces<IReadOnlyList<SpaceSummary>>();
        spaces.MapGet("/{id:guid}", GetAsync).WithMetadata(new YouthRecordAttribute(Spaces.Read)).Produces<SpaceView>();
        spaces.MapPost("/{id:guid}/posts", PostAsync).WithMetadata(new YouthRecordAttribute(Spaces.Posted)).Produces<PostView>(StatusCodes.Status201Created);
        spaces.MapDelete("/{id:guid}/posts/{postId:guid}", DeleteAsync).Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, Spaces spaces, CancellationToken ct) =>
        Results.Ok(await spaces.MineAsync(Actor(user), ct));

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, Spaces spaces, AuditTrail audit, CancellationToken ct)
    {
        Result<SpaceView> found = await spaces.GetAsync(Actor(user), id, ct);
        return found.IsSuccess ? Results.Ok(found.Value) : Refused(audit, Spaces.Read, id, found.Error);
    }

    private static async Task<IResult> PostAsync(Guid id, PostRequest body, ClaimsPrincipal user, Spaces spaces, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<PostView> posted = await spaces.PostAsync(Actor(user), id, body.Body, body.ReplyTo, ct);
        return posted.IsSuccess
            ? Results.Created($"/api/v1/spaces/{id}", posted.Value)
            : Refused(audit, Spaces.Posted, id, posted.Error);
    }

    private static async Task<IResult> DeleteAsync(Guid id, Guid postId, ClaimsPrincipal user, Spaces spaces, AuditTrail audit, CancellationToken ct)
    {
        Result deleted = await spaces.DeleteAsync(Actor(user), id, postId, ct);
        return deleted.IsSuccess ? Results.NoContent() : Refused(audit, Spaces.PostDeleted, id, deleted.Error!);
    }

    private static IResult Refused(AuditTrail audit, string action, Guid space, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "space", SubjectIds = [space], Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
