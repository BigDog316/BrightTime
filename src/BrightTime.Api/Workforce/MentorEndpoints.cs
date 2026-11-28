using System.Security.Claims;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Workforce.Mentors;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Workforce;

/// <summary>The times a mentor is usually free, replacing what was there (D129).</summary>
public sealed record AvailabilityRequest(IReadOnlyList<AvailabilitySlot>? Slots);

/// <summary>
/// Mentors (M6, D129): a mentor's own profile and availability, and an organisation's staff finding
/// mentors. Making someone a mentor is the organisation's, in the console's staff page.
/// </summary>
public static class MentorEndpoints
{
    public static RouteGroupBuilder MapMentorEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder mine = api.MapGroup("/me/mentor-profile").RequireAuthorization();
        mine.MapGet("/", MineAsync).Produces<MentorProfileView>();
        mine.MapPut("/", SaveAsync).Produces<MentorProfileView>();
        mine.MapPut("/availability", AvailabilityAsync).Produces<MentorProfileView>();

        api.MapGet("/org/{orgId:guid}/mentors/search", SearchAsync).RequireAuthorization().Produces<IReadOnlyList<MentorCard>>();
        return api;
    }

    private static async Task<IResult> MineAsync(ClaimsPrincipal user, Mentors mentors, CancellationToken ct) =>
        Own(user) ?? Results.Ok(await mentors.MineAsync(Actor(user), ct));

    private static async Task<IResult> SaveAsync(MentorProfileInput body, ClaimsPrincipal user, Mentors mentors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Own(user) ?? Answer(await mentors.SaveAsync(Actor(user), body, ct));
    }

    private static async Task<IResult> AvailabilityAsync(AvailabilityRequest body, ClaimsPrincipal user, Mentors mentors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Own(user) ?? Answer(await mentors.SetAvailabilityAsync(Actor(user), body.Slots ?? [], ct));
    }

    private static async Task<IResult> SearchAsync(
        Guid orgId, Guid? skill, int? dayOfWeek, Guid? neighbourhood, bool? @virtual, ClaimsPrincipal user, IAuthorizationService authorization, Mentors mentors, CancellationToken ct)
    {
        AuthorizationResult allowed = await authorization.AuthorizeAsync(
            user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) }, new OrganisationScopeRequirement(Permissions.Mentors.Search, OrganisationRoles.Staff));
        if (!allowed.Succeeded)
        {
            return ApiProblems.From(Error.Forbidden("mentors.forbidden", "Your role in this organisation doesn't cover finding mentors."));
        }

        return Results.Ok(await mentors.SearchAsync(new OrganisationId(orgId), new MentorQuery(skill, dayOfWeek, neighbourhood, @virtual ?? false), ct));
    }

    /// <summary>Refused unless the person may keep a mentor's profile.</summary>
    private static IResult? Own(ClaimsPrincipal user) =>
        user.HasPermission(Permissions.Mentors.ManageOwnProfile)
            ? null
            : ApiProblems.From(Error.Forbidden("mentors.not_mentor", "Only someone an organisation has made a mentor writes a mentor's profile."));

    private static IResult Answer(Result<MentorProfileView> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
