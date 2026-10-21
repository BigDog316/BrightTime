using System.Security.Claims;
using BrightTime.Api.Forms;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Enrollment.Decisions;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Decisions;

public sealed record SeatsRequest(int Capacity, int? WaitlistCapacity);

public sealed record DecideRequest(string? Decision, string? Note);

public sealed record BulkDecideRequest(IReadOnlyList<Guid>? Ids, string? Decision, string? Note);

public sealed record OfferRequest(int Count = 1);

/// <summary>
/// An organisation's applications (M3a, D48): seats and the waitlist, the queue, and decisions.
/// Program staff and admins of the organisation read and decide (<c>applications.view</c>,
/// <c>applications.decide</c>); only owners and admins set a listing's seats. Every request
/// touching an application is audited with the organisation as the actor's, refusals included.
/// </summary>
public static class DecisionEndpoints
{
    public static RouteGroupBuilder MapDecisionEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/listings/{listingId:guid}/seats", SeatsAsync);
        org.MapPut("/listings/{listingId:guid}/seats", SetSeatsAsync);
        org.MapGet("/listings/{listingId:guid}/applications", QueueAsync).Reads().Produces<IReadOnlyList<OrgApplicationSummary>>();
        org.MapGet("/applications/{id:guid}", GetAsync).Reads().Produces<OrgApplicationView>();
        org.MapPost("/applications/{id:guid}/decide", DecideAsync).Decides().Produces<OrgApplicationView>();
        org.MapPost("/applications/bulk-decide", BulkDecideAsync).Decides().Produces<IReadOnlyList<BulkOutcome>>();
        org.MapPost("/listings/{listingId:guid}/waitlist/offer", OfferAsync).Decides();
        return api;
    }

    private static RouteHandlerBuilder Reads(this RouteHandlerBuilder builder) => builder.WithMetadata(new YouthRecordAttribute(Modules.Enrollment.Decisions.Decisions.OrgRead));

    private static RouteHandlerBuilder Decides(this RouteHandlerBuilder builder) => builder.WithMetadata(new YouthRecordAttribute(Modules.Enrollment.Decisions.Decisions.Decided));

    private static async Task<IResult> SeatsAsync(Guid orgId, Guid listingId, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, CancellationToken ct)
    {
        if (!await CanAsync(user, orgId, Permissions.Applications.View, authorization))
        {
            return ApiProblems.From(Forbidden());
        }

        Result<SeatView?> found = await decisions.SeatsAsync(new OrganisationId(orgId), listingId, ct);
        return !found.IsSuccess ? ApiProblems.From(found.Error)
            : found.Value is { } view ? Results.Ok(view)
            : ApiProblems.From(Error.NotFound("seats.none", "Set this listing's seats first."));
    }

    private static async Task<IResult> SetSeatsAsync(Guid orgId, Guid listingId, SeatsRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await FormEndpoints.RefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<SeatView> set = await decisions.SetSeatsAsync(new OrganisationId(orgId), listingId, body.Capacity, body.WaitlistCapacity, ct);
        return set.IsSuccess ? Results.Ok(set.Value) : ApiProblems.From(set.Error);
    }

    private static async Task<IResult> QueueAsync(Guid orgId, Guid listingId, string? status, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, AuditTrail audit, CancellationToken ct)
    {
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Applications.View, authorization))
        {
            return Refused(audit, Modules.Enrollment.Decisions.Decisions.OrgRead, org, Forbidden());
        }

        Result<IReadOnlyList<OrgApplicationSummary>> queue = await decisions.QueueAsync(org, listingId, status, ct);
        return queue.IsSuccess ? Results.Ok(queue.Value) : Refused(audit, Modules.Enrollment.Decisions.Decisions.OrgRead, org, queue.Error);
    }

    private static async Task<IResult> GetAsync(Guid orgId, Guid id, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, AuditTrail audit, CancellationToken ct)
    {
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Applications.View, authorization))
        {
            return Refused(audit, Modules.Enrollment.Decisions.Decisions.OrgRead, org, Forbidden());
        }

        Result<OrgApplicationView> found = await decisions.GetAsync(org, id, ct);
        return found.IsSuccess ? Results.Ok(found.Value) : Refused(audit, Modules.Enrollment.Decisions.Decisions.OrgRead, org, found.Error);
    }

    private static async Task<IResult> DecideAsync(Guid orgId, Guid id, DecideRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Applications.Decide, authorization))
        {
            return Refused(audit, Modules.Enrollment.Decisions.Decisions.Decided, org, Forbidden());
        }

        Result<OrgApplicationView> decided = await decisions.DecideAsync(org, Actor(user), id, body.Decision ?? string.Empty, body.Note, ct);
        return decided.IsSuccess ? Results.Ok(decided.Value) : Refused(audit, Modules.Enrollment.Decisions.Decisions.Decided, org, decided.Error);
    }

    private static async Task<IResult> BulkDecideAsync(Guid orgId, BulkDecideRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Applications.Decide, authorization))
        {
            return Refused(audit, Modules.Enrollment.Decisions.Decisions.Decided, org, Forbidden());
        }

        Result<IReadOnlyList<BulkOutcome>> outcomes = await decisions.BulkDecideAsync(org, Actor(user), body.Ids ?? [], body.Decision ?? string.Empty, body.Note, ct);
        if (!outcomes.IsSuccess)
        {
            return Refused(audit, Modules.Enrollment.Decisions.Decisions.Decided, org, outcomes.Error);
        }

        // Refused items leave a row too, naming why, so a bulk attempt is never invisible.
        foreach (BulkOutcome refused in outcomes.Value.Where(o => o.Error is not null))
        {
            audit.Record(new AuditAccess
            {
                Action = Modules.Enrollment.Decisions.Decisions.Decided,
                Outcome = AuditOutcome.Deny,
                SubjectKind = "application",
                SubjectIds = [refused.Id],
                ActorOrgId = org,
                Reason = refused.Error,
            });
        }

        return Results.Ok(outcomes.Value);
    }

    private static async Task<IResult> OfferAsync(Guid orgId, Guid listingId, OfferRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Modules.Enrollment.Decisions.Decisions decisions, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Applications.Decide, authorization))
        {
            return Refused(audit, Modules.Enrollment.Decisions.Decisions.Decided, org, Forbidden());
        }

        Result<IReadOnlyList<Guid>> offered = await decisions.OfferAsync(org, Actor(user), listingId, body.Count, ct);
        return offered.IsSuccess ? Results.Ok(new { offered = offered.Value }) : Refused(audit, Modules.Enrollment.Decisions.Decisions.Decided, org, offered.Error);
    }

    private static async Task<bool> CanAsync(ClaimsPrincipal user, Guid orgId, string permission, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, OrganisationRoles.Staff))).Succeeded;

    private static IResult Refused(AuditTrail audit, string action, OrganisationId org, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "application", ActorOrgId = org, Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static Error Forbidden() => Error.Forbidden("decision.forbidden", "Only this organisation's staff see and decide its applications.");

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
