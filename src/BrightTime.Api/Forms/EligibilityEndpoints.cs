using System.Security.Claims;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Eligibility;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Forms;

public sealed record EligibilityRulesRequest(IReadOnlyList<RuleInput>? Rules);

/// <summary>A dry run's made-up applicant, including the badges and playlists they would hold (D67).</summary>
public sealed record EligibilityPreviewRequest(
    IReadOnlyList<RuleInput>? Rules, DateOnly? DateOfBirth, short? Grade, Guid? NeighbourhoodId,
    IReadOnlyList<Guid>? BadgeClassIds = null, IReadOnlyList<Guid>? PlaylistIds = null);

/// <summary>
/// A listing's eligibility rules (M3a, D46), managed by the organisation's owners and admins as
/// its forms are, with a dry run against a made-up young person before anything is saved.
/// </summary>
public static class EligibilityEndpoints
{
    public static RouteGroupBuilder MapEligibilityEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}/listings/{listingId:guid}/eligibility").RequireAuthorization();
        org.MapGet("/", GetAsync);
        org.MapPut("/", SetAsync);
        org.MapPost("/preview", PreviewAsync);
        return api;
    }

    private static async Task<IResult> GetAsync(Guid orgId, Guid listingId, ClaimsPrincipal user, IAuthorizationService authorization, EligibilityRules rules, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Answer(await rules.GetAsync(new OrganisationId(orgId), listingId, ct));

    private static async Task<IResult> SetAsync(Guid orgId, Guid listingId, EligibilityRulesRequest body, ClaimsPrincipal user, IAuthorizationService authorization, EligibilityRules rules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await FormEndpoints.RefusedAsync(user, orgId, authorization)
            ?? Answer(await rules.SetAsync(new OrganisationId(orgId), listingId, body.Rules ?? [], ct));
    }

    private static async Task<IResult> PreviewAsync(Guid orgId, Guid listingId, EligibilityPreviewRequest body, ClaimsPrincipal user, IAuthorizationService authorization, EligibilityRules rules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await FormEndpoints.RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<EligibilityResult> judged = await rules.PreviewAsync(new OrganisationId(orgId), listingId, body.Rules,
            new Applicant(body.DateOfBirth, body.Grade, body.NeighbourhoodId,
                new Achievements(new HashSet<Guid>(body.BadgeClassIds ?? []), new HashSet<Guid>(body.PlaylistIds ?? []))), ct);
        return judged.IsSuccess ? Results.Ok(judged.Value) : ApiProblems.From(judged.Error);
    }

    private static IResult Answer(Result<IReadOnlyList<RuleView>> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);
}
