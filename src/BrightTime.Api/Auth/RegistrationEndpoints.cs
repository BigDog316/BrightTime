using System.Globalization;
using System.Net.Mail;
using BrightTime.Hosting.Mail;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Api.Auth;

/// <summary>An address, and the month of birth (<c>yyyy-MM</c>) the app's age question asked for.</summary>
public sealed record RegisterRequest(string? Email, string? BirthMonth);

/// <summary>The token from a claim email, and the young person's new password.</summary>
public sealed record ClaimAccountRequest(string? Token, string? Password);

/// <summary>
/// Accounts from the app (docs/plan/05, "Identity and consent"): an adult, for a guardian (D34),
/// or a young person of 13 to 17 (D35), the way D23 makes one for an organisation's applicant:
/// found or made by address, with no password until its owner sets one through the emailed
/// link, which also confirms the address. And the end of a child-to-youth claim (D35).
/// </summary>
/// <remarks>
/// <para>
/// Every well-formed request is answered 202, at once, whatever the address: the account is
/// looked up or made, and the email composed, after the response (<see cref="AccountMailDispatcher"/>),
/// so neither the answer nor its timing says whether the address had an account (T21). Rate
/// limited per client address with the sign-in routes.
/// </para>
/// <para>
/// The birth month says which: under 13 is refused at once, with nothing kept or sent, since a
/// child's account is their guardian's to make (D31); 13 to 17 makes a youth account; older, or
/// no month, an adult one, which keeps no birth month. The answer depends on the month alone,
/// never on the address.
/// </para>
/// </remarks>
public static class RegistrationEndpoints
{
    public static RouteGroupBuilder MapRegistrationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder auth = api.MapGroup("/auth").RequireRateLimiting(ApiComposition.SignInLimit);
        auth.MapPost("/register", Register).AllowAnonymous();
        auth.MapPost("/claim", ClaimAsync).AllowAnonymous();
        return api;
    }

    private static IResult Register(RegisterRequest body, AccountMailDispatcher mail, ITenantContext tenant, SiteContext site, IClock clock)
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

        (DateOnly Month, AgeBand Band)? youth = null;
        if (body.BirthMonth is not null)
        {
            DateOnly today = clock.TodayIn(tenant.TimeZone);
            if (!DateOnly.TryParseExact(body.BirthMonth, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly month)
                || month > today || month < today.AddYears(-120))
            {
                return ApiProblems.Validation(new Dictionary<string, string[]> { ["birthMonth"] = ["A month as yyyy-MM, not in the future."] });
            }

            // The younger reading of the month: never a day early.
            AgeBand band = Age.BandFromBirthMonth(month, today);
            if (band.IsCoppaProtected())
            {
                return ApiProblems.From(Error.Forbidden("register.under_13", "Under 13, a parent or guardian sets up BrightTime for you."));
            }

            youth = band.IsYouth() ? (month, band) : null;
        }

        mail.RequestRegistration(tenant.TenantId, email, site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address."), tenant.Locale, youth);
        return Results.Accepted();
    }

    /// <summary>A young person completes their claim from the app: the account becomes theirs (D35).</summary>
    private static async Task<IResult> ClaimAsync(ClaimAccountRequest body, AccountClaims claims, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<UserId> claimed = await claims.CompleteAsync(body.Token ?? string.Empty, body.Password ?? string.Empty, ct);
        return claimed.IsSuccess ? Results.NoContent() : ApiProblems.From(claimed.Error);
    }
}
