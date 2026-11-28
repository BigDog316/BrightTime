using System.Security.Claims;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Workforce.Training;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api.Workforce;

/// <summary>
/// The county's training (M6, D130), as the adults who take it see it: the catalogue, a course with
/// the units they've done, marking a unit done, and how they stand against what their roles require.
/// Writing courses is the county organiser's, in the console.
/// </summary>
public static class TrainingEndpoints
{
    public static RouteGroupBuilder MapTrainingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder courses = api.MapGroup("/courses").RequireAuthorization();
        courses.MapGet("/", CatalogueAsync).Produces<IReadOnlyList<CatalogueEntry>>();
        courses.MapGet("/{slug}", CourseAsync).Produces<TrainingCourseView>();
        courses.MapPut("/{courseId:guid}/units/{unitId:guid}/complete", CompleteAsync).Produces<TrainingCourseView>();

        api.MapGet("/me/training", StatusAsync).RequireAuthorization().Produces<TrainingStatus>();
        return api;
    }

    private static async Task<IResult> CatalogueAsync(ClaimsPrincipal user, Training training, CancellationToken ct) =>
        Answer(await training.CatalogueAsync(Actor(user), ct));

    private static async Task<IResult> CourseAsync(string slug, ClaimsPrincipal user, Training training, CancellationToken ct) =>
        Answer(await training.CourseAsync(Actor(user), slug, ct));

    private static async Task<IResult> CompleteAsync(Guid courseId, Guid unitId, ClaimsPrincipal user, Training training, CancellationToken ct) =>
        Answer(await training.CompleteUnitAsync(Actor(user), courseId, unitId, ct));

    private static async Task<IResult> StatusAsync(ClaimsPrincipal user, Training training, CancellationToken ct) =>
        Results.Ok(await training.StatusAsync(Actor(user), ct));

    private static IResult Answer<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
