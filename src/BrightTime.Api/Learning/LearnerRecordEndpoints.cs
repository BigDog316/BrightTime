using System.Security.Claims;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Learning.Records;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Learning;

public sealed record SkillRequest(Guid TermId, short? Level);

public sealed record EndorseRequest(Guid EnrollmentId, Guid TermId, short? Level);

public sealed record ProjectVisibilityRequest(string? Visibility);

public sealed record ProjectReviewRequest(bool Approve, string? Note);

/// <summary>A skill or interest a young person can list, or aim a goal at, named in the request's language.</summary>
public sealed record LearningTerm(Guid Id, string Vocabulary, string Name, Guid? ParentId);

/// <summary>
/// The rest of a young person's portfolio (M4, D69): their goals, their skills (their own, and those
/// an organisation endorses), and their projects, approved by an organisation's staff before they
/// can be shown.
/// </summary>
public static class LearnerRecordEndpoints
{
    public static RouteGroupBuilder MapLearnerRecordEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder me = api.MapGroup("/me").RequireAuthorization();
        me.MapGet("/learning/terms", TermsAsync).Produces<IReadOnlyList<LearningTerm>>();
        me.MapGet("/goals", GoalsAsync).WithMetadata(new YouthRecordAttribute(Goals.Read)).Produces<IReadOnlyList<GoalView>>();
        me.MapPost("/goals", AddGoalAsync).WithMetadata(new YouthRecordAttribute(Goals.Changed)).Produces<GoalView>(StatusCodes.Status201Created);
        me.MapPut("/goals/{id:guid}", ChangeGoalAsync).WithMetadata(new YouthRecordAttribute(Goals.Changed)).Produces<GoalView>();

        me.MapGet("/skills", SkillsAsync).WithMetadata(new YouthRecordAttribute(Skills.Read)).Produces<IReadOnlyList<SkillView>>();
        me.MapPost("/skills", SetSkillAsync).WithMetadata(new YouthRecordAttribute(Skills.Changed)).Produces<SkillView>();
        me.MapDelete("/skills/{id:guid}", RemoveSkillAsync).WithMetadata(new YouthRecordAttribute(Skills.Changed));

        me.MapGet("/projects", ProjectsAsync).WithMetadata(new YouthRecordAttribute(Projects.Read)).Produces<IReadOnlyList<ProjectView>>();
        me.MapPost("/projects", AddProjectAsync).WithMetadata(new YouthRecordAttribute(Projects.Changed)).Produces<ProjectView>(StatusCodes.Status201Created);
        me.MapPut("/projects/{id:guid}", ChangeProjectAsync).WithMetadata(new YouthRecordAttribute(Projects.Changed)).Produces<ProjectView>();
        me.MapPost("/projects/{id:guid}/submit", SubmitProjectAsync).WithMetadata(new YouthRecordAttribute(Projects.Changed)).Produces<ProjectView>();
        me.MapPut("/projects/{id:guid}/visibility", ShowProjectAsync).WithMetadata(new YouthRecordAttribute(Projects.Changed)).Produces<ProjectView>();
        me.MapDelete("/projects/{id:guid}", RemoveProjectAsync).WithMetadata(new YouthRecordAttribute(Projects.Changed));

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/projects", SubmittedAsync).WithMetadata(new YouthRecordAttribute(Projects.Read)).Produces<IReadOnlyList<ProjectSubmission>>();
        org.MapPost("/projects/{id:guid}/review", ReviewAsync).WithMetadata(new YouthRecordAttribute(Projects.Reviewed));
        org.MapPost("/skills/endorse", EndorseAsync).WithMetadata(new YouthRecordAttribute(Skills.Endorsed));
        return api;
    }

    // ---- terms -----------------------------------------------------------------------------------

    /// <summary>
    /// The county's skills and interests, with the ids the routes below take (D78). The public
    /// taxonomy names terms by code and leaves skills out: they label no listing.
    /// </summary>
    private static async Task<IResult> TermsAsync(ITenantContext tenant, ITaxonomyQueries taxonomy, CancellationToken ct)
    {
        List<LearningTerm> terms = [];
        foreach (string vocabulary in (string[])["skill", "interest"])
        {
            IReadOnlyList<VocabularyTerm> found = await taxonomy.GetVocabularyAsync(vocabulary, tenant.Locale.Code, ct).ConfigureAwait(false);
            terms.AddRange(found.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.CurrentCulture)
                .Select(t => new LearningTerm(t.Id, vocabulary, t.Name, t.ParentId)));
        }

        return Results.Ok(terms);
    }

    // ---- goals -----------------------------------------------------------------------------------

    private static async Task<IResult> GoalsAsync(ClaimsPrincipal user, Goals goals, CancellationToken ct) => Results.Ok(await goals.MineAsync(Actor(user), ct));

    private static async Task<IResult> AddGoalAsync(GoalInput body, ClaimsPrincipal user, Goals goals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<GoalView> made = await goals.CreateAsync(Actor(user), body, ct);
        return made.IsSuccess ? Results.Created($"/api/v1/me/goals/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> ChangeGoalAsync(Guid id, GoalInput body, ClaimsPrincipal user, Goals goals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Answer(await goals.UpdateAsync(Actor(user), id, body, ct));
    }

    // ---- skills ----------------------------------------------------------------------------------

    private static async Task<IResult> SkillsAsync(ClaimsPrincipal user, Skills skills, CancellationToken ct) => Results.Ok(await skills.MineAsync(Actor(user), ct));

    private static async Task<IResult> SetSkillAsync(SkillRequest body, ClaimsPrincipal user, Skills skills, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Answer(await skills.SetOwnAsync(Actor(user), body.TermId, body.Level, ct));
    }

    private static async Task<IResult> RemoveSkillAsync(Guid id, ClaimsPrincipal user, Skills skills, CancellationToken ct) =>
        Done(await skills.RemoveAsync(Actor(user), id, ct));

    private static async Task<IResult> EndorseAsync(Guid orgId, EndorseRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Skills skills, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await RefusedAsync(user, orgId, authorization, Permissions.Skills.Endorse, "skills.forbidden")
            ?? Done(await skills.EndorseAsync(new OrganisationId(orgId), Actor(user), body.EnrollmentId, body.TermId, body.Level, ct));
    }

    // ---- projects --------------------------------------------------------------------------------

    private static async Task<IResult> ProjectsAsync(ClaimsPrincipal user, Projects projects, CancellationToken ct) => Results.Ok(await projects.MineAsync(Actor(user), ct));

    private static async Task<IResult> AddProjectAsync(ProjectInput body, ClaimsPrincipal user, Projects projects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<ProjectView> made = await projects.CreateAsync(Actor(user), body, ct);
        return made.IsSuccess ? Results.Created($"/api/v1/me/projects/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> ChangeProjectAsync(Guid id, ProjectInput body, ClaimsPrincipal user, Projects projects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Answer(await projects.UpdateAsync(Actor(user), id, body, ct));
    }

    private static async Task<IResult> SubmitProjectAsync(Guid id, ClaimsPrincipal user, Projects projects, CancellationToken ct) =>
        Answer(await projects.SubmitAsync(Actor(user), id, ct));

    private static async Task<IResult> ShowProjectAsync(Guid id, ProjectVisibilityRequest body, ClaimsPrincipal user, Projects projects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Answer(await projects.ShowAsync(Actor(user), id, body.Visibility, ct));
    }

    private static async Task<IResult> RemoveProjectAsync(Guid id, ClaimsPrincipal user, Projects projects, CancellationToken ct) =>
        Done(await projects.RemoveAsync(Actor(user), id, ct));

    private static async Task<IResult> SubmittedAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, Projects projects, CancellationToken ct) =>
        await RefusedAsync(user, orgId, authorization, Permissions.Projects.Review, "projects.forbidden")
            ?? Results.Ok(await projects.SubmittedAsync(new OrganisationId(orgId), ct));

    private static async Task<IResult> ReviewAsync(Guid orgId, Guid id, ProjectReviewRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Projects projects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await RefusedAsync(user, orgId, authorization, Permissions.Projects.Review, "projects.forbidden")
            ?? Done(await projects.ReviewAsync(new OrganisationId(orgId), Actor(user), id, body.Approve, body.Note, ct));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IResult Answer<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static IResult Done(Result result) => result.IsSuccess ? Results.NoContent() : ApiProblems.From(result.Error);

    /// <summary>A member of the organisation, staff or above, with the permission named.</summary>
    private static async Task<IResult?> RefusedAsync(ClaimsPrincipal user, Guid orgId, IAuthorizationService authorization, string permission, string code)
    {
        AuthorizationResult result = await authorization.AuthorizeAsync(
            user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) }, new OrganisationScopeRequirement(permission, OrganisationRoles.Staff));
        return result.Succeeded ? null : ApiProblems.From(Error.Forbidden(code, "Your role in this organisation doesn't cover that."));
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
