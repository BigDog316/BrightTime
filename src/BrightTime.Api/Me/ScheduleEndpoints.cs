using System.Security.Claims;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Localisation;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Me;

/// <summary>
/// My schedule (D97): the signed-in person's next four weeks, and the same as a calendar file.
/// Both read where and when young people will be, so both are audited against their enrolments.
/// </summary>
public static class ScheduleEndpoints
{
    public static RouteGroupBuilder MapScheduleEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/me/schedule", GetAsync).RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(Schedule.Read)).Produces<ScheduleView>();
        api.MapGet("/me/schedule/calendar", CalendarAsync).RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(Schedule.Read)).Produces(StatusCodes.Status200OK, contentType: "text/calendar");
        return api;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, Schedule schedule, AuditTrail audit, HttpResponse response, CancellationToken ct)
    {
        (ScheduleView view, IReadOnlyList<Guid> enrolments) = await schedule.GetAsync(Actor(user), ct);
        Recorded(audit, enrolments);
        response.Headers.CacheControl = "no-store";
        return Results.Ok(view);
    }

    private static async Task<IResult> CalendarAsync(ClaimsPrincipal user, Schedule schedule, SiteContext site, Text text, AuditTrail audit, HttpResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(site);
        Uri origin = site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");
        (string ics, IReadOnlyList<Guid> enrolments) = await schedule.CalendarAsync(Actor(user), text, origin, ct);
        Recorded(audit, enrolments);
        response.Headers.CacheControl = "no-store";
        return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), ListingCalendar.ContentType, "brighttime-schedule.ics");
    }

    private static void Recorded(AuditTrail audit, IReadOnlyList<Guid> enrolments)
    {
        if (enrolments.Count > 0)
        {
            audit.Record(new AuditAccess { Action = Schedule.Read, SubjectKind = "enrollment", SubjectIds = [.. enrolments] });
        }
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
