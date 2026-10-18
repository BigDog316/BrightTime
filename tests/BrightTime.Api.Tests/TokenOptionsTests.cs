using BrightTime.Api.Auth;

namespace BrightTime.Api.Tests;

public sealed class TokenOptionsTests
{
    [Fact]
    public void Outside_development_a_signing_key_is_required() =>
        Should.Throw<InvalidOperationException>(() => new TokenOptions().Keys(development: false));

    [Fact]
    public void Development_without_a_key_makes_one_for_the_run() =>
        new TokenOptions().Keys(development: true).Single().Key.Length.ShouldBe(32);

    [Fact]
    public void A_short_or_unnamed_key_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => new TokenOptions { SigningKeys = [new() { Id = "k", Key = Convert.ToBase64String(new byte[16]) }] }.Keys(false));
        Should.Throw<InvalidOperationException>(() => new TokenOptions { SigningKeys = [new() { Id = "", Key = Convert.ToBase64String(new byte[32]) }] }.Keys(false));
    }

    [Fact]
    public void Youth_sessions_lapse_sooner_than_adults()
    {
        TokenOptions options = new();
        options.LifetimeFor("youth").Idle.ShouldBe(TimeSpan.FromMinutes(30));
        options.LifetimeFor("adult").Idle.ShouldBe(TimeSpan.FromDays(30));
        options.LifetimeFor("adult").Absolute.ShouldBe(TimeSpan.FromDays(90));
    }
}
