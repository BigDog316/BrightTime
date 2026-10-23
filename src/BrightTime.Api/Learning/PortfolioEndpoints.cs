using System.Security.Claims;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Learning;

/// <summary>
/// A young person's portfolio (M4, D65): their own view and settings, records hidden or shown
/// again, and the page anyone with its link sees once they choose to show it.
/// </summary>
public static class PortfolioEndpoints
{
    public static RouteGroupBuilder MapPortfolioEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder mine = api.MapGroup("/me/portfolio").RequireAuthorization();
        mine.MapGet("/", GetAsync).WithMetadata(new YouthRecordAttribute(Portfolios.Read)).Produces<PortfolioView>();
        mine.MapPut("/", SaveAsync).WithMetadata(new YouthRecordAttribute(Portfolios.Changed)).Produces<PortfolioView>();
        mine.MapPost("/participation/{id:guid}/hide", HideAsync).WithMetadata(new YouthRecordAttribute(Portfolios.Changed)).Produces<PortfolioView>();
        mine.MapPost("/participation/{id:guid}/show", ShowAsync).WithMetadata(new YouthRecordAttribute(Portfolios.Changed)).Produces<PortfolioView>();

        // Shown by its owner's choice; never lists who they are beyond the display name they chose.
        api.MapGet("/public/portfolios/{slug}", PublicAsync).AllowAnonymous().RequireRateLimiting(Public.PublicEndpoints.RateLimit)
            .WithMetadata(new YouthRecordAttribute(Portfolios.PublicRead)).Produces<PublicPortfolio>();
        return api;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, Portfolios portfolios, CancellationToken ct) =>
        Results.Ok(await portfolios.GetMineAsync(Actor(user), ct));

    private static async Task<IResult> SaveAsync(PortfolioInput body, ClaimsPrincipal user, Portfolios portfolios, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Answer(await portfolios.SaveAsync(Actor(user), body, ct));
    }

    private static async Task<IResult> HideAsync(Guid id, ClaimsPrincipal user, Portfolios portfolios, CancellationToken ct) =>
        Answer(await portfolios.HideAsync(Actor(user), id, hidden: true, ct));

    private static async Task<IResult> ShowAsync(Guid id, ClaimsPrincipal user, Portfolios portfolios, CancellationToken ct) =>
        Answer(await portfolios.HideAsync(Actor(user), id, hidden: false, ct));

    private static async Task<IResult> PublicAsync(string slug, HttpResponse response, Portfolios portfolios, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (await portfolios.PublicAsync(slug, ct) is not { } portfolio)
        {
            return ApiProblems.From(Error.NotFound("portfolio.not_shown", "There's no portfolio shown at that link."));
        }

        // A link-only portfolio is reached by its link, never found by a search engine.
        if (!portfolio.Listed)
        {
            response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        }

        response.Headers.CacheControl = "no-store";
        return Results.Ok(portfolio);
    }

    private static IResult Answer(Result<PortfolioView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
