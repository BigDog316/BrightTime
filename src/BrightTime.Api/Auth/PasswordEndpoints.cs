using System.Net.Mail;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Mail;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api.Auth;

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? Password);

/// <summary>
/// A forgotten password from the app (docs/plan/05's <c>/auth/password/forgot | /reset</c>), as the
/// web's pages do it: the request is answered the same for every address and the email composed
/// after the response (T21); the reset link opens the web's page, or the app through this route.
/// </summary>
/// <remarks>Rate limited with the sign-in routes. A reset ends every session, as on the web.</remarks>
public static class PasswordEndpoints
{
    public static RouteGroupBuilder MapPasswordEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder password = api.MapGroup("/auth/password").RequireRateLimiting(ApiComposition.SignInLimit);
        password.MapPost("/forgot", Forgot).AllowAnonymous();
        password.MapPost("/reset", ResetAsync).AllowAnonymous();
        return api;
    }

    private static IResult Forgot(ForgotPasswordRequest body, AccountMailDispatcher mail, ITenantContext tenant, SiteContext site)
    {
        ArgumentNullException.ThrowIfNull(body);
        string email = body.Email?.Trim() ?? string.Empty;
        if (email.Length is 0 or > AccountProvisioning.MaxEmailLength || !MailAddress.TryCreate(email, out _))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["email"] = ["An email address."] });
        }

        mail.RequestReset(tenant.TenantId, email, site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address."), tenant.Locale);
        return Results.Accepted();
    }

    private static async Task<IResult> ResetAsync(
        ResetPasswordRequest body,
        PasswordAccounts passwords,
        IBreachedPasswordCheck breached,
        ITenantContext tenant,
        AuditTrail audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        string password = body.Password ?? string.Empty;
        if (await breached.IsBreachedAsync(password, ct))
        {
            return ApiProblems.From(Breached());
        }

        Result<UserId> reset = await passwords.ResetAsync(tenant.TenantId, body.Token ?? string.Empty, password, ct);
        if (!reset.IsSuccess)
        {
            return ApiProblems.From(reset.Error);
        }

        audit.Record(AuthAudit.Allowed(AuthAudit.ResetCompleted, reset.Value, detail: TokenEndpoints.Channel(null)));
        return Results.NoContent();
    }

    /// <summary>A password on a breach list, refused as the web refuses it.</summary>
    internal static Error Breached() =>
        Error.Validation("password.breached", "That password has appeared in a data breach elsewhere. Choose another.");
}
