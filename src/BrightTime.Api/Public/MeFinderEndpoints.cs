using System.Security.Claims;
using BrightTime.Hosting.Auth;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Personal;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api.Public;

public sealed record SaveRequest(string? Note);

public sealed record SavedItemResponse(ListingSummaryResponse Listing, string? Note, DateTimeOffset SavedAt);

public sealed record InterestWeight(string? Code, int Weight);

public sealed record InterestsRequest(IReadOnlyList<InterestWeight>? Interests);

public sealed record InterestResponse(string Code, string Name, int Weight);

public sealed record SearchPreferencesRequest(string? NearNeighbourhood, int? MaxDistanceMetres);

public sealed record SearchPreferencesResponse(string? NearNeighbourhood, int? MaxDistanceMetres);

public sealed record ReminderRequest(Guid ListingId, string? Anchor, int LeadMinutes);

public sealed record ReminderResponse(Guid Id, Guid ListingId, string Anchor, int LeadMinutes, DateTimeOffset RemindAt, bool Sent);

/// <summary>
/// The signed-in person's own finder data (docs/plan/05, "Opportunities"): saved listings that
/// follow them between devices, interests, where they search from, and the feed those make.
/// Listings come back in the same shape as the public search, "why" included.
/// </summary>
public static class MeFinderEndpoints
{
    public static RouteGroupBuilder MapMeFinderEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder me = api.MapGroup("/me").RequireAuthorization();
        me.MapGet("/saved-items", SavedAsync);
        me.MapPut("/saved-items/{listingId:guid}", SaveAsync);
        me.MapDelete("/saved-items/{listingId:guid}", RemoveAsync);
        me.MapGet("/interests", InterestsAsync);
        me.MapPut("/interests", SetInterestsAsync);
        me.MapGet("/search-preferences", PreferencesAsync);
        me.MapPut("/search-preferences", SetPreferencesAsync);
        me.MapGet("/feed", FeedAsync);
        me.MapGet("/reminders", RemindersAsync);
        me.MapPost("/reminders", RemindAsync);
        me.MapDelete("/reminders/{id:guid}", ForgetReminderAsync);
        return api;
    }

    private static async Task<IResult> SavedAsync(ClaimsPrincipal user, PersonalFinder personal, IListingFinder finder,
        IOrganisationProfiles organisations, Infrastructure.Tenancy.ITenantContext tenant, CancellationToken ct)
    {
        IReadOnlyList<SavedEntry> saved = await personal.SavedAsync(Actor(user), ct);
        if (saved.Count == 0)
        {
            return Results.Ok(Array.Empty<SavedItemResponse>());
        }

        // Through the finder, so a listing that is no longer published drops out of the list.
        FinderPage found = await finder.SearchAsync(new SearchQuery
        {
            TenantId = tenant.TenantId.Value,
            TextSearchConfig = tenant.Locale.TextSearchConfig,
            ListingIds = [.. saved.Select(s => s.ListingId)],
            PageSize = new PageSize(PageSize.Max),
        }, ct);
        Dictionary<Guid, ListingSummaryResponse> byId = (await PublicEndpoints.SummariesAsync(found.Items, organisations, ct)).ToDictionary(s => s.Id);

        return Results.Ok(saved
            .Where(s => byId.ContainsKey(s.ListingId))
            .Select(s => new SavedItemResponse(byId[s.ListingId], s.Note, s.SavedAt))
            .ToList());
    }

    private static async Task<IResult> SaveAsync(Guid listingId, SaveRequest? body, ClaimsPrincipal user, PersonalFinder personal, CancellationToken ct)
    {
        Result<SavedEntry> saved = await personal.SaveAsync(Actor(user), listingId, body?.Note, ct);
        return saved.IsSuccess ? Results.NoContent() : ApiProblems.From(saved.Error);
    }

    private static async Task<IResult> RemoveAsync(Guid listingId, ClaimsPrincipal user, PersonalFinder personal, CancellationToken ct) =>
        await personal.RemoveAsync(Actor(user), listingId, ct)
            ? Results.NoContent()
            : ApiProblems.NotFound("That listing is not saved.");

    private static async Task<IResult> InterestsAsync(ClaimsPrincipal user, PersonalFinder personal, CancellationToken ct) =>
        Results.Ok((await personal.InterestsAsync(Actor(user), ct)).Select(i => new InterestResponse(i.Code, i.Name, i.Weight)).ToList());

    private static async Task<IResult> SetInterestsAsync(InterestsRequest body, ClaimsPrincipal user, PersonalFinder personal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        IReadOnlyList<InterestWeight> given = body.Interests ?? [];
        if (given.Any(i => string.IsNullOrWhiteSpace(i.Code)) || given.Select(i => i.Code).Distinct(StringComparer.Ordinal).Count() != given.Count)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["interests"] = ["Each interest once, by its code."] });
        }

        Result<IReadOnlyList<InterestEntry>> set = await personal.SetInterestsAsync(Actor(user), given.ToDictionary(i => i.Code!, i => i.Weight, StringComparer.Ordinal), ct);
        return set.IsSuccess
            ? Results.Ok(set.Value.Select(i => new InterestResponse(i.Code, i.Name, i.Weight)).ToList())
            : ApiProblems.From(set.Error);
    }

    private static async Task<IResult> PreferencesAsync(ClaimsPrincipal user, PersonalFinder personal, CancellationToken ct)
    {
        SearchAnchor anchor = await personal.AnchorAsync(Actor(user), ct);
        return Results.Ok(new SearchPreferencesResponse(anchor.NeighbourhoodCode, anchor.MaxDistanceMetres));
    }

    private static async Task<IResult> SetPreferencesAsync(SearchPreferencesRequest body, ClaimsPrincipal user, PersonalFinder personal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<SearchAnchor> set = await personal.SetAnchorAsync(Actor(user), new SearchAnchor(body.NearNeighbourhood, body.MaxDistanceMetres), ct);
        return set.IsSuccess
            ? Results.Ok(new SearchPreferencesResponse(set.Value.NeighbourhoodCode, set.Value.MaxDistanceMetres))
            : ApiProblems.From(set.Error);
    }

    private static async Task<IResult> FeedAsync(string? cursor, int? limit, ClaimsPrincipal user, PersonalFinder personal, IListingFinder finder,
        IOrganisationProfiles organisations, CancellationToken ct)
    {
        if (limit is < 1 or > PageSize.Max)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["limit"] = [$"A whole number from 1 to {PageSize.Max}."] });
        }

        List<PhraseResponse> notices = [];
        Cursor? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (Cursor.TryDecode(cursor, out Cursor decoded))
            {
                after = decoded;
            }
            else
            {
                notices.Add(PhraseResponse.From(SharedKernel.Text.Phrase.Of("finder.notice.cursor_expired")));
            }
        }

        SearchQuery query = await personal.FeedQueryAsync(Actor(user), after, new PageSize(limit ?? PageSize.Default), ct);
        FinderPage page = await finder.SearchAsync(query, ct);
        return Results.Ok(new PageResponse<ListingSummaryResponse>(await PublicEndpoints.SummariesAsync(page.Items, organisations, ct), page.NextCursor, notices));
    }

    private static async Task<IResult> RemindersAsync(ClaimsPrincipal user, ListingReminders reminders, CancellationToken ct) =>
        Results.Ok((await reminders.ListAsync(Actor(user), ct)).Select(Reminder).ToList());

    private static async Task<IResult> RemindAsync(ReminderRequest body, ClaimsPrincipal user, ListingReminders reminders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<ReminderView> made = await reminders.CreateAsync(Actor(user), body.ListingId, body.Anchor ?? string.Empty, body.LeadMinutes, ct);
        return made.IsSuccess
            ? Results.Created($"/api/v1/me/reminders/{made.Value.Id}", Reminder(made.Value))
            : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> ForgetReminderAsync(Guid id, ClaimsPrincipal user, ListingReminders reminders, CancellationToken ct) =>
        await reminders.DeleteAsync(Actor(user), id, ct) ? Results.NoContent() : ApiProblems.NotFound("You have no reminder with that id.");

    private static ReminderResponse Reminder(ReminderView r) => new(r.Id, r.ListingId, r.Anchor, r.LeadMinutes, r.RemindAt, r.Sent);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
