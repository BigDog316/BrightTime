using System.Security.Claims;
using BrightTime.Hosting.Auditing;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Api.Me;

/// <summary>An organisation the person works for, and their role there.</summary>
public sealed record MeOrganisation(Guid OrganisationId, string Role);

/// <summary>The signed-in person as the app needs them: who, what kind of account, and what they may do.</summary>
public sealed record MeResponse(
    Guid Id,
    string UserName,
    string AccountKind,
    string? Locale,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<MeOrganisation> Organisations);

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder me = api.MapGroup("/me").RequireAuthorization();
        me.MapGet("/", Get);
        me.MapDelete("/sessions", SignOutEverywhereAsync);
        return api;
    }

    /// <summary>Built from the principal, which every request rebuilds from the database.</summary>
    private static MeResponse Get(ClaimsPrincipal user) => new(
        user.GetUserId() ?? Guid.Empty,
        user.Identity?.Name ?? string.Empty,
        user.FindFirstValue(BrightTimeClaims.AccountKind) ?? string.Empty,
        user.FindFirstValue(BrightTimeClaims.Locale),
        [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal)],
        [.. user.FindAll(BrightTimeClaims.Permission).Select(c => c.Value).Order(StringComparer.Ordinal)],
        [.. user.FindAll(BrightTimeClaims.OrganisationMembership)
            .Select(c => BrightTimeClaims.TryParseMembership(c.Value, out Guid org, out string role) ? new MeOrganisation(org, role) : null)
            .OfType<MeOrganisation>()]);

    /// <summary>Every session ends: refresh tokens are revoked and the security stamp changes, which also ends web sessions.</summary>
    private static async Task<IResult> SignOutEverywhereAsync(ClaimsPrincipal user, TokenSessions sessions, TenantContext tenant, AuditTrail audit, CancellationToken ct)
    {
        UserId id = new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
        await sessions.SignOutEverywhereAsync(tenant.TenantId, id, ct);
        audit.Record(AuthAudit.Allowed(AuthAudit.SignOutEverywhere, id, detail: new Dictionary<string, string> { ["channel"] = "api" }));
        return Results.NoContent();
    }
}
