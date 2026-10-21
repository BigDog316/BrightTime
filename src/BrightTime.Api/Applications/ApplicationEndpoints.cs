using System.Security.Claims;
using System.Text.Json;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Applications;

public sealed record NewApplicationRequest(Guid ListingId, Guid MemberId);

public sealed record AnswersRequest(Dictionary<string, JsonElement>? Answers);

public sealed record SubmitRequest(IReadOnlyList<string>? ShareFields, DateTimeOffset? ShareExpiresAt);

/// <summary>
/// A family's applications (M3a, D46). Every request reads or changes a young person's
/// application, so every one is audited, refusals included.
/// </summary>
public static class ApplicationEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static RouteGroupBuilder MapApplicationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder applications = api.MapGroup("/applications").RequireAuthorization();
        applications.MapPost("/", CreateAsync).Changes().Produces<ApplicationView>(StatusCodes.Status201Created);
        applications.MapGet("/", ListAsync).Reads().Produces<IReadOnlyList<ApplicationSummary>>();
        applications.MapGet("/{id:guid}", GetAsync).Reads().Produces<ApplicationView>();
        applications.MapPatch("/{id:guid}", SaveAnswersAsync).Changes().Produces<ApplicationView>();
        applications.MapPost("/{id:guid}/submit", SubmitAsync).Changes().Produces<ApplicationView>();
        applications.MapPost("/{id:guid}/withdraw", WithdrawAsync).Changes();
        return api;
    }

    private static RouteHandlerBuilder Reads(this RouteHandlerBuilder builder) => builder.WithMetadata(new YouthRecordAttribute(Modules.Enrollment.Applications.Applications.Read));

    private static RouteHandlerBuilder Changes(this RouteHandlerBuilder builder) => builder.WithMetadata(new YouthRecordAttribute(Modules.Enrollment.Applications.Applications.Changed));

    private static async Task<IResult> CreateAsync(NewApplicationRequest body, HttpRequest request, ClaimsPrincipal user, Modules.Enrollment.Applications.Applications applications, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(request);
        if (ChildRefused(user, audit, Modules.Enrollment.Applications.Applications.Changed) is { } refused)
        {
            return refused;
        }

        Result<(ApplicationView View, bool Created)> made = await applications.CreateAsync(Owner(user), body.ListingId, body.MemberId, request.Headers[IdempotencyHeader].ToString(), ct);
        if (!made.IsSuccess)
        {
            return Refused(audit, Modules.Enrollment.Applications.Applications.Changed, made.Error);
        }

        return made.Value.Created
            ? Results.Created($"/api/v1/applications/{made.Value.View.Id}", made.Value.View)
            : Results.Ok(made.Value.View);
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, Modules.Enrollment.Applications.Applications applications, AuditTrail audit, CancellationToken ct) =>
        ChildRefused(user, audit, Modules.Enrollment.Applications.Applications.Read) ?? Results.Ok(await applications.ListAsync(Owner(user), ct));

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, Modules.Enrollment.Applications.Applications applications, AuditTrail audit, CancellationToken ct) =>
        ChildRefused(user, audit, Modules.Enrollment.Applications.Applications.Read)
            ?? Answer(audit, Modules.Enrollment.Applications.Applications.Read, await applications.GetAsync(Owner(user), id, ct));

    private static async Task<IResult> SaveAnswersAsync(Guid id, AnswersRequest body, ClaimsPrincipal user, Modules.Enrollment.Applications.Applications applications, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ChildRefused(user, audit, Modules.Enrollment.Applications.Applications.Changed)
            ?? Answer(audit, Modules.Enrollment.Applications.Applications.Changed, await applications.SaveAnswersAsync(Owner(user), id, body.Answers ?? [], ct));
    }

    private static async Task<IResult> SubmitAsync(Guid id, SubmitRequest body, ClaimsPrincipal user, Modules.Enrollment.Applications.Applications applications, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ChildRefused(user, audit, Modules.Enrollment.Applications.Applications.Changed)
            ?? Answer(audit, Modules.Enrollment.Applications.Applications.Changed,
                await applications.SubmitAsync(Owner(user), id, new SubmitInput(body.ShareFields ?? [], body.ShareExpiresAt), ct));
    }

    private static async Task<IResult> WithdrawAsync(Guid id, ClaimsPrincipal user, Modules.Enrollment.Applications.Applications applications, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, Modules.Enrollment.Applications.Applications.Changed) is { } refused)
        {
            return refused;
        }

        Result withdrawn = await applications.WithdrawAsync(Owner(user), id, ct);
        return withdrawn.IsSuccess ? Results.NoContent() : Refused(audit, Modules.Enrollment.Applications.Applications.Changed, withdrawn.Error);
    }

    private static IResult Answer(AuditTrail audit, string action, Result<ApplicationView> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Refused(audit, action, result.Error);

    private static IResult Refused(AuditTrail audit, string action, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "application", Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static IResult? ChildRefused(ClaimsPrincipal user, AuditTrail audit, string action) =>
        user.FindFirstValue(BrightTimeClaims.AccountKind) == "child"
            ? Refused(audit, action, Error.Forbidden("family.child_account", "A child's details are kept on their guardian's family profile."))
            : null;

    private static UserId Owner(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
