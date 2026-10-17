using System.Globalization;
using System.Security.Claims;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>How a person proved who they are.</summary>
public static class AuthenticationMethods
{
    public const string Password = "password";
    public const string Passkey = "passkey";
    public const string External = "external";

    /// <summary>Development sign-in (D14). Never present outside Development.</summary>
    public const string Development = "development";
}

/// <summary>
/// Step-up (threat model T21): the person proved who they are recently, not merely at some
/// point in an eight-hour session. For actions that would let a borrowed or stolen session
/// take an account over or reach a child's data: changing how the account signs in today, and
/// consent grants, guardian verification and youth-record export as those arrive.
/// </summary>
public sealed class RecentAuthenticationRequirement(TimeSpan within) : IAuthorizationRequirement
{
    public TimeSpan Within { get; } = within;
}

public sealed class RecentAuthenticationHandler(IClock clock) : AuthorizationHandler<RecentAuthenticationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RecentAuthenticationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.GetAuthenticatedAt() is { } at && clock.UtcNow - at <= requirement.Within)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.StepUpRequired,
                "Confirm it's you to continue."));
        }

        return Task.CompletedTask;
    }
}

public static class AuthenticationClaimExtensions
{
    public static DateTimeOffset? GetAuthenticatedAt(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirstValue(BrightTimeClaims.AuthTime), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    public static string? GetAuthenticationMethod(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(BrightTimeClaims.AuthMethod);
    }
}
