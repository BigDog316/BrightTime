using System.Text.Json;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Net.Http.Headers;

namespace BrightTime.Api.Public;

/// <summary>
/// <c>/api/v1/public</c>: what anyone may read, with no account, for the app and third parties
/// (docs/plan/05). The same tenant-by-host, the same services and the same visibility rules as
/// the web's finder pages, so the two cannot disagree about what is published.
/// </summary>
/// <remarks>
/// No youth data reaches this surface: it reads published listings, curation, public geography
/// and organisations' public profiles. Responses may be cached for a minute, and vary by
/// language.
/// </remarks>
public static class PublicEndpoints
{
    public const string RateLimit = "public";

    /// <summary>The vocabularies a client may read: those the finder filters or labels by.</summary>
    public static readonly IReadOnlySet<string> Vocabularies =
        new HashSet<string>(["category", "interest", "accommodation", "language", "audience"], StringComparer.Ordinal);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static RouteGroupBuilder MapPublicEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder pub = api.MapGroup("/public")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimit)
            .AddEndpointFilter(async (context, next) =>
            {
                object? result = await next(context).ConfigureAwait(false);
                context.HttpContext.Response.Headers[HeaderNames.CacheControl] = "public, max-age=60";
                return result;
            });

        pub.MapGet("/listings", SearchAsync);
        pub.MapGet("/listings/{slug}", ListingAsync);
        pub.MapGet("/collections", CollectionsAsync);
        pub.MapGet("/collections/{slug}", CollectionAsync);
        pub.MapGet("/neighbourhoods", NeighbourhoodsAsync);
        pub.MapGet("/neighbourhoods/{code}", NeighbourhoodAsync);
        pub.MapGet("/organisations/{slug}", OrganisationAsync);
        pub.MapGet("/taxonomy/{vocabulary}", TaxonomyAsync);
        pub.MapGet("/tenant", Tenant);
        return api;
    }

    private static async Task<IResult> SearchAsync(
        HttpRequest request, ITenantContext tenant, IListingFinder finder, IGeographyQueries geography,
        IOrganisationProfiles organisations, CancellationToken ct)
    {
        Dictionary<string, string[]> values = request.Query.ToDictionary(q => q.Key, q => q.Value.Where(v => v is not null).Select(v => v!).ToArray(), StringComparer.Ordinal);

        IReadOnlyList<NeighbourhoodShape> hoods = values.ContainsKey("nearNeighbourhood")
            ? await geography.GetNeighbourhoodsAsync(tenant.Locale.Code, ct).ConfigureAwait(false)
            : [];

        PublicSearchParse parsed = PublicSearchRequest.Parse(
            values, tenant.TenantId.Value, tenant.Locale,
            code => hoods.FirstOrDefault(h => string.Equals(h.Code, code, StringComparison.OrdinalIgnoreCase))?.Id);

        if (parsed.Query is null)
        {
            return ApiProblems.Validation(parsed.Errors);
        }

        FinderPage page = await finder.SearchAsync(parsed.Query, ct).ConfigureAwait(false);
        IReadOnlyList<ListingSummaryResponse> items = await SummariesAsync(page.Items, organisations, ct).ConfigureAwait(false);
        return Results.Ok(new PageResponse<ListingSummaryResponse>(items, page.NextCursor, [.. parsed.Notices.Select(PhraseResponse.From)]));
    }

    private static async Task<IResult> ListingAsync(string slug, ITenantContext tenant, IListingDetails details, IOrganisationProfiles organisations, CancellationToken ct)
    {
        ListingDetail? d = await details.GetPublishedAsync(tenant.TenantId, slug, tenant.Locale.Code, ct).ConfigureAwait(false);
        if (d is null)
        {
            return ApiProblems.NotFound("No published listing has that address.");
        }

        OrganisationLink? link = null;
        if (d.OrganisationId is { } orgId)
        {
            (await organisations.GetLinksAsync([orgId], ct).ConfigureAwait(false)).TryGetValue(orgId, out link);
        }

        return Results.Ok(new ListingDetailResponse(
            d.Id.Value, d.Slug, d.Kind.ToDbValue(), d.Title, d.Summary, d.Description,
            d.ContentLanguage, d.DescriptionLanguage, d.IsMachineTranslated,
            new OrganisationRef(d.OrganisationId?.Value ?? Guid.Empty, link?.Slug ?? string.Empty, link?.DisplayName ?? d.OrganisationName),
            d.AgeMin, d.AgeMax, d.GradeMin, d.GradeMax,
            d.CostModel.ToDbValue(), d.CostCentsMin, d.CostCentsMax, d.SlidingScaleNote,
            d.Modality.ToDbValue(), d.RegistrationMode.ToDbValue(), d.ExternalUrl,
            d.MealsProvided, d.TransportProvided, d.TransportNote, d.IsDropIn, d.AccessibilityNote,
            d.ContactEmail, d.ContactPhone, d.Categories, d.Languages, d.Accommodations,
            [.. d.Places.Select(p => new PlaceResponse(
                p.Name, p.IsAddressPublic,
                // The source strips a private address (SqlListingDetails); this is the second lock.
                p.IsAddressPublic ? p.AddressLine1 : null,
                p.IsAddressPublic ? p.AddressLine2 : null,
                p.IsAddressPublic ? p.Locality : null,
                p.IsAddressPublic ? p.PostalCode : null,
                p.NeighbourhoodName,
                p.IsAddressPublic ? p.TransitNote : null,
                p.IsWheelchairAccessible))],
            [.. d.Upcoming.Select(t => new SessionResponse(t.StartsAt, t.EndsAt))],
            [.. d.Deadlines.Select(x => new DeadlineResponse(x.Kind, x.DueAt, x.Label))]));
    }

    private static async Task<IResult> CollectionsAsync(bool? featured, ITenantContext tenant, ICurationQueries curation, CancellationToken ct)
    {
        IReadOnlyList<CollectionSummary> found = await curation.ListCollectionsAsync(tenant.Locale.Code, featured ?? false, ct).ConfigureAwait(false);
        return Results.Ok(found.Select(c => new CollectionSummaryResponse(c.Slug, c.Title, c.Description, c.ContentLanguage, c.ListingCount)).ToList());
    }

    private static async Task<IResult> CollectionAsync(
        string slug, ITenantContext tenant, ICurationQueries curation, IListingFinder finder,
        IOrganisationProfiles organisations, CancellationToken ct)
    {
        CollectionView? collection = await curation.GetCollectionAsync(slug, tenant.Locale.Code, ct).ConfigureAwait(false);
        if (collection is null)
        {
            return ApiProblems.NotFound("No collection has that address.");
        }

        // Through the finder, so an entry whose listing is no longer published drops out.
        List<Guid> order = [.. collection.Entries.Select(e => e.ListingId)];
        FinderPage found = order.Count == 0
            ? FinderPage.Empty
            : await finder.SearchAsync(new SearchQuery
            {
                TenantId = tenant.TenantId.Value,
                TextSearchConfig = tenant.Locale.TextSearchConfig,
                ListingIds = order,
                PageSize = new PageSize(PageSize.Max),
            }, ct).ConfigureAwait(false);

        IReadOnlyList<ListingSummaryResponse> summaries = await SummariesAsync(found.Items, organisations, ct).ConfigureAwait(false);
        Dictionary<Guid, ListingSummaryResponse> byId = summaries.ToDictionary(s => s.Id);
        List<CollectionItemResponse> items = [.. collection.Entries
            .Where(e => byId.ContainsKey(e.ListingId))
            .Select(e => new CollectionItemResponse(byId[e.ListingId], e.CuratorNote))];

        return Results.Ok(new CollectionResponse(collection.Slug, collection.Title, collection.Description, collection.ContentLanguage, items));
    }

    private static async Task<IResult> NeighbourhoodsAsync(ITenantContext tenant, IGeographyQueries geography, IListingFinder finder, CancellationToken ct)
    {
        IReadOnlyList<NeighbourhoodShape> shapes = await geography.GetNeighbourhoodsAsync(tenant.Locale.Code, ct).ConfigureAwait(false);
        NeighbourhoodCounts counts = await finder.CountByNeighbourhoodAsync(Everything(tenant), ct).ConfigureAwait(false);
        return Results.Ok(shapes
            .OrderBy(s => s.Name, StringComparer.CurrentCulture)
            .Select(s => new NeighbourhoodSummaryResponse(
                s.Code, s.Name, counts.ByNeighbourhood.GetValueOrDefault(s.Id), new CoordinateResponse(s.CentroidLongitude, s.CentroidLatitude)))
            .ToList());
    }

    private static async Task<IResult> NeighbourhoodAsync(
        string code, ITenantContext tenant, IGeographyQueries geography, IListingFinder finder, ICurationQueries curation, CancellationToken ct)
    {
        IReadOnlyList<NeighbourhoodShape> shapes = await geography.GetNeighbourhoodsAsync(tenant.Locale.Code, ct).ConfigureAwait(false);
        NeighbourhoodShape? shape = shapes.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
        if (shape is null)
        {
            return ApiProblems.NotFound("No neighbourhood has that code.");
        }

        NeighbourhoodCounts counts = await finder.CountByNeighbourhoodAsync(Everything(tenant), ct).ConfigureAwait(false);
        NeighbourhoodPageView? page = await curation.GetNeighbourhoodPageAsync(shape.Id, tenant.Locale.Code, ct).ConfigureAwait(false);

        return Results.Ok(new NeighbourhoodResponse(
            shape.Code,
            shape.Name,
            counts.ByNeighbourhood.GetValueOrDefault(shape.Id),
            new CoordinateResponse(shape.CentroidLongitude, shape.CentroidLatitude),
            [.. shape.Rings.Select(r => (IReadOnlyList<double[]>)[.. r.Select(p => new[] { p.Longitude, p.Latitude })])],
            page?.ContentLanguage,
            [.. (page?.Blocks ?? []).Select(b => new PageBlockResponse(b.Kind, b.Heading, b.Body, Payload(b.Payload)))]));
    }

    private static async Task<IResult> OrganisationAsync(string slug, ITenantContext tenant, IOrganisationProfiles organisations, IGeographyQueries geography, CancellationToken ct)
    {
        OrganisationProfile? org = await organisations.GetAsync(slug, ct).ConfigureAwait(false);
        if (org is null)
        {
            return ApiProblems.NotFound("No organisation has that address.");
        }

        Guid[] hoodIds = [.. org.Sites.Select(s => s.NeighbourhoodId).OfType<Guid>().Distinct()];
        IReadOnlyDictionary<Guid, string> hoodNames = hoodIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await geography.GetNeighbourhoodNamesAsync(hoodIds, tenant.Locale.Code, ct).ConfigureAwait(false);

        return Results.Ok(new OrganisationResponse(
            org.Id.Value, org.Slug, org.DisplayName, org.Kind, org.Mission, org.WebsiteUrl, org.PublicEmail, org.PublicPhone,
            org.LanguagesServed,
            [.. org.Sites.Select(s => new SiteResponse(
                s.Name,
                s.NeighbourhoodId is { } h && hoodNames.TryGetValue(h, out string? name) ? name : null,
                s.IsAddressPublic,
                s.IsAddressPublic ? s.AddressLine1 : null,
                s.IsAddressPublic ? s.AddressLine2 : null,
                s.IsAddressPublic ? s.Locality : null,
                s.IsAddressPublic ? s.PostalCode : null,
                s.IsAddressPublic ? s.TransitNote : null,
                s.IsWheelchairAccessible, s.HasAccessibleRestroom, s.HasElevator, s.HasQuietSpace))]));
    }

    private static async Task<IResult> TaxonomyAsync(string vocabulary, ITenantContext tenant, ITaxonomyQueries taxonomy, CancellationToken ct)
    {
        if (!Vocabularies.Contains(vocabulary))
        {
            return ApiProblems.NotFound("No such vocabulary. One of: " + string.Join(", ", Vocabularies.Order(StringComparer.Ordinal)) + ".");
        }

        IReadOnlyList<VocabularyTerm> terms = await taxonomy.GetVocabularyAsync(vocabulary, tenant.Locale.Code, ct).ConfigureAwait(false);
        Dictionary<Guid, string> codes = terms.ToDictionary(t => t.Id, t => t.Code);
        return Results.Ok(terms
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.CurrentCulture)
            .Select(t => new TermResponse(t.Code, t.Name, t.ParentId is { } p && codes.TryGetValue(p, out string? parent) ? parent : null, t.SortOrder))
            .ToList());
    }

    private static IResult Tenant(SiteContext site, ITenantContext tenant)
    {
        TenantSite? s = site.Site;
        if (s is null)
        {
            return ApiProblems.NotFound("No site at this address.");
        }

        IReadOnlyList<Locale> locales = s.EnabledLocales;
        return Results.Ok(new TenantResponse(
            s.DisplayName,
            s.DefaultLocale,
            tenant.TimeZone.Id,
            [.. locales.Select(l => new LocaleResponse(l.Code, l.NativeName, l.Direction))]));
    }

    /// <summary>Every published listing of the tenant, for counts.</summary>
    private static SearchQuery Everything(ITenantContext tenant) =>
        new() { TenantId = tenant.TenantId.Value, TextSearchConfig = tenant.Locale.TextSearchConfig };

    internal static async Task<IReadOnlyList<ListingSummaryResponse>> SummariesAsync(
        IReadOnlyList<FinderItem> items, IOrganisationProfiles organisations, CancellationToken ct)
    {
        IReadOnlyDictionary<OrganisationId, OrganisationLink> links = items.Count == 0
            ? new Dictionary<OrganisationId, OrganisationLink>()
            : await organisations.GetLinksAsync([.. items.Select(i => i.OrganisationId).Distinct()], ct).ConfigureAwait(false);

        return [.. items.Select(i => new ListingSummaryResponse(
            i.ListingId.Value,
            i.Slug,
            i.Kind.ToDbValue(),
            i.Title,
            i.Summary,
            links.TryGetValue(i.OrganisationId, out OrganisationLink? l) ? new OrganisationRef(l.Id.Value, l.Slug, l.DisplayName) : null,
            new CostResponse(i.IsFree, i.IsFree ? null : i.CostCents),
            i.NextStartAt,
            i.NextDeadlineAt,
            i.DistanceMetres,
            [.. i.Reasons.Select(PhraseResponse.From)]))];
    }

    private static JsonElement Payload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }
}
