using System.Net.Mail;
using BrightTime.Hosting.Mail;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.SignIn;

namespace BrightTime.Api.Auth;

public sealed record RegisterRequest(string? Email);

/// <summary>
/// An adult account from the app, for a guardian (docs/plan/05, "Identity and consent"), the
/// way D23 makes one for an organisation's applicant: found or made by address, with no
/// password until its owner sets one through the emailed link, which also confirms the address.
/// </summary>
/// <remarks>
/// Every well-formed request is answered 202, at once, whatever the address: the account is
/// looked up or made, and the email composed, after the response (<see cref="AccountMailDispatcher"/>),
/// so neither the answer nor its timing says whether the address had an account (T21). Rate
/// limited per client address with the sign-in routes.
/// </remarks>
public static class RegistrationEndpoints
{
    public static RouteGroupBuilder MapRegistrationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGroup("/auth").RequireRateLimiting(ApiComposition.SignInLimit)
            .MapPost("/register", Register).AllowAnonymous();
        return api;
    }

    private static IResult Register(RegisterRequest body, AccountMailDispatcher mail, ITenantContext tenant, SiteContext site)
    {
        ArgumentNullException.ThrowIfNull(body);
        string email = body.Email?.Trim() ?? string.Empty;

        // Only the shape is checked here; that says nothing about who has an account.
        if (email.Length is 0 or > AccountProvisioning.MaxEmailLength
            || !MailAddress.TryCreate(email, out MailAddress? parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["email"] = ["An email address."] });
        }

        mail.RequestRegistration(tenant.TenantId, email, site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address."), tenant.Locale);
        return Results.Accepted();
    }
}
