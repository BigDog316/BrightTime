using System.Globalization;
using System.Security.Claims;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Tests;

public sealed class RecentAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

        public DateOnly TodayIn(TimeZoneInfo timeZone) => Today;
    }

    private static async Task<bool> SucceedsAsync(ClaimsPrincipal user)
    {
        var requirement = new RecentAuthenticationRequirement(TimeSpan.FromMinutes(10));
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await new RecentAuthenticationHandler(new FixedClock()).HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal SignedIn(DateTimeOffset? at) =>
        new(new ClaimsIdentity(
            at is { } t ? [new Claim(BrightTimeClaims.AuthTime, t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))] : [],
            "test"));

    [Fact]
    public async Task Within_the_window_succeeds() =>
        (await SucceedsAsync(SignedIn(Now.AddMinutes(-9)))).ShouldBeTrue();

    [Fact]
    public async Task Past_the_window_fails() =>
        (await SucceedsAsync(SignedIn(Now.AddMinutes(-11)))).ShouldBeFalse();

    [Fact]
    public async Task No_authentication_time_fails() =>
        (await SucceedsAsync(SignedIn(null))).ShouldBeFalse();

    [Fact]
    public async Task A_garbled_authentication_time_fails()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(BrightTimeClaims.AuthTime, "yesterday")], "test"));
        (await SucceedsAsync(user)).ShouldBeFalse();
    }
}
