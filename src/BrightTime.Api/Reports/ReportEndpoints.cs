using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Attendance.Reports;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Reporting;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Reports;

public sealed record ReportRunRequest(string? ReportKey, Guid? ListingId, DateTimeOffset? From, DateTimeOffset? To, string? Format);

/// <summary>
/// An organisation's reports (M3b, D57): roster, attendance and participation, as PDF, Excel or
/// CSV. Each run is made when it is asked for and goes straight back. Nothing rendered is stored,
/// so no copy of a roster is left waiting in a bucket.
/// </summary>
public static class ReportEndpoints
{
    public const string RunAction = "reports.run";

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}/reports").RequireAuthorization();
        org.MapGet("/catalogue", CatalogueAsync);
        org.MapPost("/runs", RunAsync).WithMetadata(new YouthRecordAttribute(RunAction));
        return api;
    }

    private static async Task<IResult> CatalogueAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, ProviderReportRunner runner, CancellationToken ct) =>
        await CanAsync(user, orgId, Permissions.Reports.RunProvider, authorization)
            ? Results.Ok(await runner.CatalogueAsync(await CanAsync(user, orgId, Permissions.Attendance.ViewRoster, authorization), ct))
            : ApiProblems.From(Forbidden());

    /// <summary>Runs a report and returns the file. A report that names people also needs the roster permission.</summary>
    private static async Task<IResult> RunAsync(Guid orgId, ReportRunRequest body, ClaimsPrincipal user, IAuthorizationService authorization,
        ProviderReportRunner runner, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        if (ProviderReportRunner.Check(body.ReportKey, body.ListingId, body.Format) is { } invalid && invalid.Code == "reports.unknown")
        {
            return ApiProblems.From(invalid);
        }

        string action = ProviderReports.AuditAction(body.ReportKey!);
        if (!await CanAsync(user, orgId, Permissions.Reports.RunProvider, authorization)
            || (ProviderReportKeys.NamesPeople(body.ReportKey!) && !await CanAsync(user, orgId, Permissions.Attendance.ViewRoster, authorization)))
        {
            return Refused(audit, action, org, Forbidden());
        }

        UserId actor = new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
        Result<RenderedReport> made = await runner.RunAsync(org, actor, body.ReportKey, body.ListingId, body.From, body.To, body.Format, ct);
        return made.IsSuccess ? new ReportFile(made.Value) : Refused(audit, action, org, made.Error);
    }

    private static async Task<bool> CanAsync(ClaimsPrincipal user, Guid orgId, string permission, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, OrganisationRoles.Staff))).Succeeded;

    private static Error Forbidden() => Error.Forbidden("reports.forbidden", "Only staff of this organisation run its reports.");

    private static IResult Refused(AuditTrail audit, string action, OrganisationId org, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "enrollment", ActorOrgId = org, Reason = error.Code });
        return ApiProblems.From(error);
    }

    /// <summary>The file, never cached: it can name young people.</summary>
    private sealed class ReportFile(RenderedReport report) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(report.Bytes, report.ContentType, report.FileName).ExecuteAsync(httpContext);
        }
    }
}
