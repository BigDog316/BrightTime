using System.Security.Claims;
using BrightTime.Hosting.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Reporting;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Api.Learning;

public sealed record PortfolioExportRequest(string? Format);

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

        // A file made now and not kept (D68): the printable portfolio, the badges as OB 3.0, or a CLR 2.0.
        mine.MapPost("/export", ExportAsync).WithMetadata(new YouthRecordAttribute(PortfolioExports.Exported));

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

    private static async Task<IResult> ExportAsync(
        PortfolioExportRequest body, ClaimsPrincipal user, SiteContext site, PortfolioExports exports, Text text, IClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(clock);
        if (PortfolioExports.Check(body.Format) is { } invalid)
        {
            return ApiProblems.From(invalid);
        }

        Uri address = site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");
        if (body.Format == PortfolioExportFormats.Pdf)
        {
            PortfolioFacts facts = await exports.FactsAsync(Actor(user), address, ct);
            await text.LoadAsync(ct);
            ReportDocument document = PortfolioDocuments.Word(facts, text, clock.UtcNow);
            return Results.File(ReportRenderer.Render(document, ReportFormats.Pdf), ReportFormats.ContentType(ReportFormats.Pdf), document.FileName + ".pdf");
        }

        Result<ExportFile> made = await exports.CredentialsAsync(Actor(user), address, site.Site?.DisplayName ?? address.Host, body.Format!, ct);
        return made.IsSuccess ? Results.File(made.Value.Bytes, made.Value.ContentType, made.Value.FileName) : ApiProblems.From(made.Error);
    }

    private static IResult Answer(Result<PortfolioView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
