using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Infrastructure.Tests;

public sealed class TenantContextTests
{
    private static readonly TenantId TenantA = TenantId.New();
    private static readonly TenantId TenantB = TenantId.New();

    [Fact]
    public void Starts_unresolved()
    {
        var context = new TenantContext();

        context.IsResolved.ShouldBeFalse();
        context.HasActor.ShouldBeFalse();
        context.ActorKind.ShouldBe(ActorKind.Anonymous);
    }

    [Fact]
    public void Resolving_twice_to_the_same_tenant_is_harmless()
    {
        var context = new TenantContext();

        context.SetTenant(TenantA, TimeZoneInfo.Utc, Locale.English);
        context.SetTenant(TenantA, TimeZoneInfo.Utc, Locale.Spanish);

        context.TenantId.ShouldBe(TenantA);
        context.Locale.ShouldBe(Locale.Spanish);
    }

    [Fact]
    public void Switching_tenant_mid_request_throws()
    {
        // By the time a second tenant arrives, rows have already been read
        // under the first. Continuing would mix them; failing is the only safe
        // option.
        var context = new TenantContext();
        context.SetTenant(TenantA, TimeZoneInfo.Utc, Locale.English);

        Should.Throw<InvalidOperationException>(
            () => context.SetTenant(TenantB, TimeZoneInfo.Utc, Locale.English));
    }

    [Fact]
    public void Refuses_an_empty_tenant() =>
        Should.Throw<ArgumentException>(
            () => new TenantContext().SetTenant(TenantId.None, TimeZoneInfo.Utc, Locale.English));

    [Fact]
    public void An_anonymous_actor_cannot_carry_a_user_id() =>
        Should.Throw<ArgumentException>(
            () => new TenantContext().SetActor(UserId.New(), ActorKind.Anonymous));

    [Fact]
    public void A_user_actor_requires_a_user_id() =>
        Should.Throw<ArgumentException>(
            () => new TenantContext().SetActor(UserId.None, ActorKind.User));

    [Fact]
    public void A_job_actor_needs_no_user_id()
    {
        // Background work has no human behind it, which is why the RESTRICTIVE
        // "requires an authenticated actor" policy excludes brighttime_jobs.
        var context = new TenantContext();
        context.SetActor(UserId.None, ActorKind.Job);

        context.ActorKind.ShouldBe(ActorKind.Job);
        context.HasActor.ShouldBeFalse();
    }
}
