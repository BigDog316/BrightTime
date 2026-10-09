using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Infrastructure.Tenancy;

/// <summary>
/// The resolved tenant and actor for the current request or job.
/// </summary>
/// <remarks>
/// Scoped, set once by tenant resolution, read by the connection interceptor
/// that issues the <c>SET LOCAL</c> for row-level security. Deliberately not an
/// ambient <see cref="System.Threading.AsyncLocal{T}"/>: an ambient tenant is
/// how a background continuation ends up writing to the wrong one.
/// </remarks>
public interface ITenantContext
{
    TenantId TenantId { get; }

    /// <summary>The tenant's own timezone. "Due today" is a wall-clock question in the region.</summary>
    TimeZoneInfo TimeZone { get; }

    Locale Locale { get; }

    /// <summary>The authenticated account, or <see cref="UserId.None"/> for an anonymous public request.</summary>
    UserId ActorId { get; }

    /// <summary>What kind of principal this is, written to <c>audit_event.actor_kind</c>.</summary>
    ActorKind ActorKind { get; }

    bool IsResolved { get; }

    bool HasActor { get; }
}

public enum ActorKind
{
    Anonymous,
    User,
    Service,
    Job,
    Integration,
}

/// <summary>The writable view, available only to resolution middleware and job setup.</summary>
public sealed class TenantContext : ITenantContext
{
    public TenantId TenantId { get; private set; } = TenantId.None;

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Utc;

    public Locale Locale { get; private set; } = Locale.Default;

    public UserId ActorId { get; private set; } = UserId.None;

    public ActorKind ActorKind { get; private set; } = ActorKind.Anonymous;

    public bool IsResolved => !TenantId.IsNone;

    public bool HasActor => !ActorId.IsNone;

    public void SetTenant(TenantId tenantId, TimeZoneInfo timeZone, Locale locale)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(locale);

        if (tenantId.IsNone)
        {
            throw new ArgumentException("Cannot resolve to an empty tenant.", nameof(tenantId));
        }

        if (IsResolved && TenantId != tenantId)
        {
            // A request that switches tenant mid-flight has already read rows
            // under the old context. Fail loudly rather than carry on.
            throw new InvalidOperationException(
                $"Tenant context is already resolved to {TenantId} and cannot be changed to {tenantId}.");
        }

        TenantId = tenantId;
        TimeZone = timeZone;
        Locale = locale;
    }

    public void SetActor(UserId actorId, ActorKind kind)
    {
        if (kind == ActorKind.Anonymous && !actorId.IsNone)
        {
            throw new ArgumentException("An anonymous actor cannot carry a user id.", nameof(kind));
        }

        if (kind is ActorKind.User && actorId.IsNone)
        {
            throw new ArgumentException("A user actor requires a user id.", nameof(actorId));
        }

        ActorId = actorId;
        ActorKind = kind;
    }

    public void SetLocale(Locale locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        Locale = locale;
    }
}
