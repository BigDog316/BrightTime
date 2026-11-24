using System.Security.Claims;
using BrightTime.Hosting.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Modules.Learning.Records;
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

        // A child profile's portfolio, for their verified guardian (D93): read, and downloaded as D68's files.
        RouteGroupBuilder child = api.MapGroup("/children/{id:guid}/portfolio").RequireAuthorization();
        child.MapGet("/", ChildAsync).WithMetadata(new YouthRecordAttribute(ChildPortfolios.Read)).Produces<ChildPortfolioView>();
        child.MapPost("/export", ChildExportAsync).WithMetadata(new YouthRecordAttribute(PortfolioExports.Exported));

        // Shown by its owner's choice; never lists who they are beyond the display name they chose.
        api.MapGet("/public/portfolios/{slug}", PublicAsync).AllowAnonymous().RequireRateLimiting(Public.PublicEndpoints.RateLimit)
            .WithMetadata(new YouthRecordAttribute(Portfolios.PublicRead)).Produces<PublicPortfolio>();
        // A published project's pictures (D104), on the same terms, and kept by nothing between.
        api.MapGet("/public/portfolios/{slug}/projects/{id:guid}/pictures/{mediaId:guid}", PublicPictureAsync).AllowAnonymous()
            .RequireRateLimiting(Public.PublicEndpoints.RateLimit).WithMetadata(new YouthRecordAttribute(Portfolios.PublicRead));
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

    private static async Task<IResult> PublicPictureAsync(string slug, Guid id, Guid mediaId, HttpResponse response, Projects projects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (await projects.PublicPictureAsync(slug, id, mediaId, ct) is not { } file)
        {
            return Results.NotFound();
        }

        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return Results.File(file.Bytes, file.ContentType);
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

        Uri address = Address(site);
        if (body.Format == PortfolioExportFormats.Pdf)
        {
            return await PdfAsync(await exports.FactsAsync(Actor(user), address, ct), text, clock, ct);
        }

        return File(await exports.CredentialsAsync(Actor(user), address, SiteName(site, address), body.Format!, ct));
    }

    private static async Task<IResult> ChildAsync(Guid id, ClaimsPrincipal user, SiteContext site, ChildPortfolios children, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(site);
        Result<ChildPortfolioView> view = await children.GetAsync(Actor(user), new UserId(id), Address(site), ct);
        return view.IsSuccess ? Results.Ok(view.Value) : ApiProblems.From(view.Error);
    }

    private static async Task<IResult> ChildExportAsync(
        Guid id, PortfolioExportRequest body, ClaimsPrincipal user, SiteContext site, ChildPortfolios children, Text text, IClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(clock);
        if (PortfolioExports.Check(body.Format) is { } invalid)
        {
            return ApiProblems.From(invalid);
        }

        Uri address = Address(site);
        UserId child = new(id);
        if (body.Format == PortfolioExportFormats.Pdf)
        {
            Result<PortfolioFacts> facts = await children.FactsAsync(Actor(user), child, address, ct);
            return facts.IsSuccess ? await PdfAsync(facts.Value, text, clock, ct) : ApiProblems.From(facts.Error);
        }

        return File(await children.CredentialsAsync(Actor(user), child, address, SiteName(site, address), body.Format!, ct));
    }

    private static async Task<IResult> PdfAsync(PortfolioFacts facts, Text text, IClock clock, CancellationToken ct)
    {
        await text.LoadAsync(ct);
        ReportDocument document = PortfolioDocuments.Word(facts, text, clock.UtcNow);
        return Results.File(ReportRenderer.Render(document, ReportFormats.Pdf), ReportFormats.ContentType(ReportFormats.Pdf), document.FileName + ".pdf");
    }

    private static IResult File(Result<ExportFile> made) =>
        made.IsSuccess ? Results.File(made.Value.Bytes, made.Value.ContentType, made.Value.FileName) : ApiProblems.From(made.Error);

    private static Uri Address(SiteContext site) => site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");

    private static string SiteName(SiteContext site, Uri address) => site.Site?.DisplayName ?? address.Host;

    private static IResult Answer(Result<PortfolioView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
