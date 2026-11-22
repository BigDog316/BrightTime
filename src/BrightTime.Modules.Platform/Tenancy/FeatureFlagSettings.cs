using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Tenancy;

/// <summary>
/// Switches a county's flags on or off from the organiser console (D99). Each key changed is
/// audited with what it became and who changed it; a key left as it was is not.
/// </summary>
public sealed class FeatureFlagSettings(PlatformDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public const string Changed = "platform.feature_flag.changed";

    /// <summary>Sets each flag given; returns how many changed.</summary>
    public Task<int> SetAsync(IReadOnlyDictionary<string, bool> flags, UserId actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flags);
        return db.InTransactionAsync(async ct =>
        {
            string[] keys = [.. flags.Keys];
            Dictionary<string, FeatureFlag> held = await db.FeatureFlags.Where(f => keys.Contains(f.Key)).ToDictionaryAsync(f => f.Key, StringComparer.Ordinal, ct).ConfigureAwait(false);
            DateTimeOffset now = clock.UtcNow;
            Dictionary<string, string> changed = new(StringComparer.Ordinal);
            foreach ((string key, bool on) in flags)
            {
                if (held.TryGetValue(key, out FeatureFlag? flag))
                {
                    if (flag.IsEnabled == on)
                    {
                        continue;
                    }

                    flag.IsEnabled = on;
                    flag.UpdatedAt = now;
                    flag.UpdatedBy = actor.Value;
                }
                else if (on)
                {
                    db.FeatureFlags.Add(new FeatureFlag { TenantId = tenant.TenantId.Value, Key = key, IsEnabled = true, UpdatedAt = now, UpdatedBy = actor.Value });
                }
                else
                {
                    continue;
                }

                changed[key] = on ? "on" : "off";
            }

            if (changed.Count == 0)
            {
                return 0;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Changed, ActorId = actor, Detail = changed }, ct).ConfigureAwait(false);
            return changed.Count;
        }, cancellationToken);
    }
}
