using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Me;

/// <summary>
/// "Who has seen my record" (D96): a person's own log, and a child profile's for their verified
/// guardian. Reading either is itself audited.
/// </summary>
/// <remarks>
/// docs/plan/04's <c>audit.read</c> is "own" and "children", so the check is whose record it is,
/// not a role: a family's adult holds no role until they work for an organisation, as D93's
/// guardian routes already assume.
/// </remarks>
public static class RecordLogEndpoints
{
    public static RouteGroupBuilder MapRecordLogEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/me/record-log", MineAsync).RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(RecordLog.Read)).Produces<RecordLogPage>();
        api.MapGet("/children/{id:guid}/record-log", ChildAsync).RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(RecordLog.Read)).Produces<RecordLogPage>();
        return api;
    }

    private static async Task<IResult> MineAsync(string? older, ClaimsPrincipal user, RecordLog log, AuditTrail audit, CancellationToken ct)
    {
        UserId me = Actor(user);
        RecordLogPage page = await log.MineAsync(me, older, ct);
        Recorded(audit, me);
        return Results.Ok(page);
    }

    private static async Task<IResult> ChildAsync(Guid id, string? older, ClaimsPrincipal user, RecordLog log, AuditTrail audit, CancellationToken ct)
    {
        UserId child = new(id);
        Result<RecordLogPage> page = await log.ChildAsync(Actor(user), child, older, ct);
        if (!page.IsSuccess)
        {
            audit.Record(new AuditAccess
            {
                Action = RecordLog.Read,
                Outcome = AuditOutcome.Deny,
                SubjectKind = "user",
                SubjectIds = [id],
                Reason = page.Error.Code,
            });
            return ApiProblems.From(page.Error);
        }

        Recorded(audit, child);
        return Results.Ok(page.Value);
    }

    private static void Recorded(AuditTrail audit, UserId subject) =>
        audit.Record(new AuditAccess { Action = RecordLog.Read, SubjectKind = "user", SubjectIds = [subject.Value] });

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
