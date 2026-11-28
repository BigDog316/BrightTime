using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Community.Showcasing;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Groups;

/// <summary>A young person's answer to a proposal for the showcase (D124): yes shows it, no declines it.</summary>
public sealed record ShowcaseAnswer(bool Show);

/// <summary>A page of the public showcase, and where the next one starts (null when this is the last).</summary>
[YouthData]
public sealed record ShowcasePage(IReadOnlyList<ShownWork> Items, DateTimeOffset? Before);

/// <summary>
/// The showcase (M5, D124): the public page anyone reads, and a young person's own items, which
/// they say yes or no to and take down. Proposing and taking down for an organisation or the
/// county are the console's.
/// </summary>
public static class ShowcaseEndpoints
{
    public static RouteGroupBuilder MapShowcaseEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/public/showcase", PublicAsync).AllowAnonymous().RequireRateLimiting(Public.PublicEndpoints.RateLimit)
            .WithMetadata(new YouthRecordAttribute(Showcase.PublicRead)).Produces<ShowcasePage>();

        RouteGroupBuilder mine = api.MapGroup("/me/showcase").RequireAuthorization();
        mine.MapGet("/", MineAsync).WithMetadata(new YouthRecordAttribute(Showcase.Read)).Produces<IReadOnlyList<ShowcaseEntry>>();
        mine.MapPost("/{id:guid}/answer", AnswerAsync).WithMetadata(new YouthRecordAttribute(Showcase.Published)).Produces<ShowcaseEntry>();
        mine.MapPost("/{id:guid}/take-down", TakeDownAsync).WithMetadata(new YouthRecordAttribute(Showcase.TakenDown)).Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static async Task<IResult> PublicAsync(DateTimeOffset? before, HttpResponse response, Showcase showcase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        IReadOnlyList<ShownWork> items = await showcase.ShownAsync(before, Showcase.PageSize, ct);

        // What's shown is its author's to take back at once.
        response.Headers.CacheControl = "no-store";
        return Results.Ok(new ShowcasePage(items, items.Count == Showcase.PageSize ? items[^1].ShownAt : null));
    }

    private static async Task<IResult> MineAsync(ClaimsPrincipal user, Showcase showcase, CancellationToken ct) =>
        Results.Ok(await showcase.MineAsync(Actor(user), ct));

    private static async Task<IResult> AnswerAsync(Guid id, ShowcaseAnswer body, ClaimsPrincipal user, Showcase showcase, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<ShowcaseEntry> answered = await showcase.AnswerAsync(Actor(user), id, body.Show, ct);
        return answered.IsSuccess ? Results.Ok(answered.Value) : Refused(audit, body.Show ? Showcase.Published : Showcase.Declined, id, answered.Error);
    }

    private static async Task<IResult> TakeDownAsync(Guid id, ClaimsPrincipal user, Showcase showcase, AuditTrail audit, CancellationToken ct)
    {
        Result taken = await showcase.TakeDownMineAsync(Actor(user), id, ct);
        return taken.IsSuccess ? Results.NoContent() : Refused(audit, Showcase.TakenDown, id, taken.Error!);
    }

    private static IResult Refused(AuditTrail audit, string action, Guid item, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "showcase_item", SubjectIds = [item], Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
