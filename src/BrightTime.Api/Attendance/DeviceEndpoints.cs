using System.Security.Claims;
using BrightTime.Api.Forms;
using BrightTime.Api.Public;
using BrightTime.Modules.Attendance.Devices;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Attendance;

public sealed record RegisterDeviceRequest(string? DeviceId, string? Label, Guid? SiteId, string? AppVersion);

public sealed record RevokeDeviceRequest(string? Reason);

public sealed record DeviceContactRequest(string? DeviceId, string? AppVersion);

/// <summary>
/// The phones and kiosks an organisation lets sync attendance (M3b, D51): registered by its
/// staff, listed for its admins, revoked (with a wipe) by an admin or by whoever registered the
/// device, and told on every contact whether to wipe (docs/plan/07, T12).
/// </summary>
public static class DeviceEndpoints
{
    public static RouteGroupBuilder MapDeviceEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}/kiosk-devices").RequireAuthorization();
        org.MapGet("/", ListAsync);
        org.MapPost("/", RegisterAsync);
        org.MapPost("/{id:guid}/revoke", RevokeAsync);

        // Needs no sign-in: a device whose user has left must still learn to wipe.
        api.MapPost("/attendance/devices/contact", ContactAsync).AllowAnonymous().RequireRateLimiting(PublicEndpoints.RateLimit);
        return api;
    }

    private static async Task<IResult> ListAsync(Guid orgId, ClaimsPrincipal user, IAuthorizationService authorization, KioskDevices devices, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Results.Ok(await devices.ListAsync(new OrganisationId(orgId), ct));

    private static async Task<IResult> RegisterAsync(Guid orgId, RegisterDeviceRequest body, ClaimsPrincipal user, IAuthorizationService authorization, KioskDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!await CanAsync(user, orgId, Permissions.Attendance.OfflineSync, OrganisationRoles.Staff, authorization))
        {
            return ApiProblems.From(Forbidden());
        }

        Result<(DeviceView View, bool Created)> registered = await devices.RegisterAsync(new OrganisationId(orgId), Actor(user),
            body.DeviceId ?? string.Empty, body.Label ?? string.Empty, body.SiteId, body.AppVersion, ct);
        return !registered.IsSuccess ? ApiProblems.From(registered.Error)
            : registered.Value.Created ? Results.Created($"/api/v1/org/{orgId}/kiosk-devices/{registered.Value.View.Id}", registered.Value.View)
            : Results.Ok(registered.Value.View);
    }

    private static async Task<IResult> RevokeAsync(Guid orgId, Guid id, RevokeDeviceRequest body, ClaimsPrincipal user, IAuthorizationService authorization, KioskDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        bool admin = await CanAsync(user, orgId, Permissions.Organisations.Edit, OrganisationRoles.Admin, authorization);
        if (!admin && !await CanAsync(user, orgId, Permissions.Attendance.OfflineSync, OrganisationRoles.Staff, authorization))
        {
            return ApiProblems.From(Forbidden());
        }

        Result<DeviceView> revoked = await devices.RevokeAsync(new OrganisationId(orgId), Actor(user), admin, id, body.Reason ?? string.Empty, ct);
        return revoked.IsSuccess ? Results.Ok(revoked.Value) : ApiProblems.From(revoked.Error);
    }

    private static async Task<IResult> ContactAsync(DeviceContactRequest body, ClaimsPrincipal user, KioskDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        List<Guid> memberOf = [.. user.FindAll(BrightTimeClaims.OrganisationMembership)
            .Select(c => BrightTimeClaims.TryParseMembership(c.Value, out Guid org, out _) ? org : Guid.Empty)
            .Where(g => g != Guid.Empty)];
        return Results.Ok(await devices.ContactAsync(body.DeviceId ?? string.Empty, body.AppVersion, memberOf, ct));
    }

    private static async Task<bool> CanAsync(ClaimsPrincipal user, Guid orgId, string permission, string role, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(permission, role))).Succeeded;

    private static Error Forbidden() => Error.Forbidden("attendance.forbidden", "Only this organisation's staff take and see its attendance.");

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
