using System.Globalization;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Reports;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Reporting;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Web.Auth;

/// <summary>
/// The console's report download (D58): the Reports page's forms post here, and the file comes
/// straight back, never cached and never stored (D57). A page can't answer with a file, so this is
/// an endpoint, with the same checks as the API's.
/// </summary>
public static class ReportDownloads
{
    public static IEndpointRouteBuilder MapReportDownloads(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/provider/orgs/{orgId:guid}/reports/download", DownloadAsync)
            .RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute("reports.run"));
        return endpoints;
    }

    private static async Task<IResult> DownloadAsync(Guid orgId, HttpContext http, IAntiforgery antiforgery, IAuthorizationService authorization,
        ITenantContext tenant, ProviderReportRunner runner, AuditTrail audit)
    {
        // Validated by hand, then the form read: see MapBrightTimeSignIn.
        if (!http.Request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        string key = form["reportKey"].ToString();
        string page = $"/provider/orgs/{orgId}/reports";
        OrganisationId org = new(orgId);
        if (ProviderReportRunner.Check(key, Guid.TryParse(form["listingId"], out Guid parsed) ? parsed : null, form["format"]) is { Code: "reports.unknown" } unknown)
        {
            return Results.LocalRedirect($"{page}?problem={Uri.EscapeDataString(unknown.Code)}");
        }

        string action = ProviderReports.AuditAction(key);
        if (!await CanAsync(http, orgId, Permissions.Reports.RunProvider, authorization).ConfigureAwait(false)
            || (ProviderReportKeys.NamesPeople(key) && !await CanAsync(http, orgId, Permissions.Attendance.ViewRoster, authorization).ConfigureAwait(false)))
        {
            audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "enrollment", ActorOrgId = org, Reason = "reports.forbidden" });
            return Results.Forbid();
        }

        // The page's dates are days in the tenant's zone; the period runs from the first day's start to the last day's end.
        DateTimeOffset? from = Day(form["from"], tenant.TimeZone);
        DateTimeOffset? to = Day(form["to"], tenant.TimeZone)?.AddDays(1);
        Result<RenderedReport> made = await runner.RunAsync(org, new UserId(http.User.GetUserId()!.Value), key,
            Guid.TryParse(form["listingId"], out Guid listing) ? listing : null, from, to, form["format"], http.RequestAborted).ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "enrollment", ActorOrgId = org, Reason = made.Error.Code });
            return Results.LocalRedirect($"{page}?problem={Uri.EscapeDataString(made.Error.Code)}");
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.File(made.Value.Bytes, made.Value.ContentType, made.Value.FileName);
    }

    private static DateTimeOffset? Day(string? value, TimeZoneInfo zone)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            return null;
        }

        DateTime midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    private static async Task<bool> CanAsync(HttpContext http, Guid orgId, string permission, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(http.User, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, OrganisationRoles.Staff)).ConfigureAwait(false)).Succeeded;
}
