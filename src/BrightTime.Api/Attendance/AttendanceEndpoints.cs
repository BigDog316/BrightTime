using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Attendance;

public sealed record AttendanceRequest(
    Guid EnrollmentId,
    DateTimeOffset OccurrenceStartsAt,
    string? State,
    short? MinutesAttended,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    string? Note);

public sealed record SyncItemRequest(
    Guid ClientRecordId,
    Guid EnrollmentId,
    DateTimeOffset OccurrenceStartsAt,
    string? State,
    short? MinutesAttended,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    string? Note,
    DateTimeOffset? CapturedAt);

public sealed record SyncRequest(string? DeviceId, string? AppVersion, IReadOnlyList<SyncItemRequest>? Items);

public sealed record CorrectionRequest(string? State, short? MinutesAttended, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt, string? Note, string? Reason);

/// <summary>
/// Attendance (M3b, D49): an organisation's rosters, records, offline sync and corrections, and a
/// family's view of its own. Every request reads or writes where a named child was, so every one is
/// audited, refusals included (docs/plan/07, T4).
/// </summary>
public static class AttendanceEndpoints
{
    public static RouteGroupBuilder MapAttendanceEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/listings/{listingId:guid}/roster", RosterAsync).Audits(AttendanceRecords.RosterRead).Produces<RosterView>();
        org.MapPost("/attendance", RecordAsync).Audits(AttendanceRecords.Recorded).Produces<AttendanceView>(StatusCodes.Status201Created);
        org.MapPost("/attendance/batch", SyncAsync).Audits(AttendanceRecords.Synced).Produces<IReadOnlyList<SyncOutcome>>();
        org.MapGet("/attendance", ListAsync).Audits(AttendanceRecords.Read).Produces<IReadOnlyList<AttendanceView>>();
        org.MapPatch("/attendance/{id:guid}", CorrectAsync).Audits(AttendanceRecords.Corrected).Produces<AttendanceView>();

        api.MapGet("/me/attendance", FamilyAsync).RequireAuthorization().Audits(AttendanceRecords.FamilyRead).Produces<IReadOnlyList<FamilyAttendance>>();
        return api;
    }

    private static RouteHandlerBuilder Audits(this RouteHandlerBuilder builder, string action) => builder.WithMetadata(new YouthRecordAttribute(action));

    private static async Task<IResult> RosterAsync(Guid orgId, Guid listingId, DateTimeOffset occurrence, ClaimsPrincipal user, IAuthorizationService authorization, AttendanceRecords attendance, AuditTrail audit, CancellationToken ct)
    {
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Attendance.ViewRoster, authorization))
        {
            return Refused(audit, AttendanceRecords.RosterRead, org, Forbidden());
        }

        Result<RosterView> roster = await attendance.RosterAsync(org, listingId, occurrence, ct);
        return roster.IsSuccess ? Results.Ok(roster.Value) : Refused(audit, AttendanceRecords.RosterRead, org, roster.Error);
    }

    private static async Task<IResult> RecordAsync(Guid orgId, AttendanceRequest body, ClaimsPrincipal user, IAuthorizationService authorization, AttendanceRecords attendance, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Attendance.Record, authorization))
        {
            return Refused(audit, AttendanceRecords.Recorded, org, Forbidden());
        }

        Result<AttendanceView> recorded = await attendance.RecordAsync(org, Actor(user),
            new AttendanceInput(body.EnrollmentId, body.OccurrenceStartsAt, body.State ?? string.Empty, body.MinutesAttended, body.CheckedInAt, body.CheckedOutAt, body.Note), ct);
        return recorded.IsSuccess
            ? Results.Created($"/api/v1/org/{orgId}/attendance/{recorded.Value.Id}", recorded.Value)
            : Refused(audit, AttendanceRecords.Recorded, org, recorded.Error);
    }

    /// <summary>A phone's offline queue. Each item gets its own outcome; duplicates are a success.</summary>
    private static async Task<IResult> SyncAsync(Guid orgId, SyncRequest body, ClaimsPrincipal user, IAuthorizationService authorization, AttendanceRecords attendance, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Attendance.OfflineSync, authorization))
        {
            return Refused(audit, AttendanceRecords.Synced, org, Forbidden());
        }

        List<SyncItem> items = [.. (body.Items ?? []).Select(i => new SyncItem(i.ClientRecordId,
            new AttendanceInput(i.EnrollmentId, i.OccurrenceStartsAt, i.State ?? string.Empty, i.MinutesAttended, i.CheckedInAt, i.CheckedOutAt, i.Note), i.CapturedAt))];
        Result<IReadOnlyList<SyncOutcome>> synced = await attendance.SyncAsync(org, Actor(user), body.DeviceId ?? string.Empty, body.AppVersion, items, ct);
        return synced.IsSuccess ? Results.Ok(synced.Value) : Refused(audit, AttendanceRecords.Synced, org, synced.Error);
    }

    private static async Task<IResult> ListAsync(Guid orgId, DateTimeOffset from, DateTimeOffset to, Guid? listingId, bool? history, ClaimsPrincipal user, IAuthorizationService authorization, AttendanceRecords attendance, AuditTrail audit, CancellationToken ct)
    {
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Attendance.ViewRoster, authorization))
        {
            return Refused(audit, AttendanceRecords.Read, org, Forbidden());
        }

        Result<IReadOnlyList<AttendanceView>> found = await attendance.ListAsync(org, from, to, listingId, history ?? false, ct);
        return found.IsSuccess ? Results.Ok(found.Value) : Refused(audit, AttendanceRecords.Read, org, found.Error);
    }

    private static async Task<IResult> CorrectAsync(Guid orgId, Guid id, CorrectionRequest body, ClaimsPrincipal user, IAuthorizationService authorization, AttendanceRecords attendance, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (!await CanAsync(user, orgId, Permissions.Attendance.Record, authorization))
        {
            return Refused(audit, AttendanceRecords.Corrected, org, Forbidden());
        }

        Result<AttendanceView> corrected = await attendance.CorrectAsync(org, Actor(user), id,
            new AttendanceInput(Guid.Empty, default, body.State ?? string.Empty, body.MinutesAttended, body.CheckedInAt, body.CheckedOutAt, body.Note), body.Reason ?? string.Empty, ct);
        return corrected.IsSuccess ? Results.Ok(corrected.Value) : Refused(audit, AttendanceRecords.Corrected, org, corrected.Error);
    }

    private static async Task<IResult> FamilyAsync(ClaimsPrincipal user, AttendanceRecords attendance, CancellationToken ct) =>
        Results.Ok(await attendance.ForFamilyAsync(Actor(user), ct));

    private static async Task<bool> CanAsync(ClaimsPrincipal user, Guid orgId, string permission, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, OrganisationRoles.Staff))).Succeeded;

    private static IResult Refused(AuditTrail audit, string action, OrganisationId org, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "enrollment", ActorOrgId = org, Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static Error Forbidden() => Error.Forbidden("attendance.forbidden", "Only this organisation's staff take and see its attendance.");

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
