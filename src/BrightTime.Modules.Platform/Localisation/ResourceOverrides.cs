using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Localisation;

/// <summary>One override, as the wording page lists it.</summary>
public sealed record WordingOverride(string Locale, string Key, string Value, DateTimeOffset UpdatedAt, Guid? UpdatedBy);

/// <summary>
/// Reads and writes <c>platform.resource_override</c> for the request's tenant.
/// </summary>
/// <remarks>
/// It stores whatever it is given. Whether a value is acceptable (a known key, the same
/// placeholders as the shipped text) depends on the catalogue, which lives in the host, so the
/// host checks before it calls. Every change is audited in its own transaction (D19).
/// </remarks>
public sealed class ResourceOverrides(PlatformDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public const int MaxValueLength = 4000;

    public Task<IReadOnlyList<WordingOverride>> ListAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<WordingOverride>>(async ct =>
            await db.ResourceOverrides.AsNoTracking()
                .OrderBy(o => o.Key).ThenBy(o => o.Locale)
                .Select(o => new WordingOverride(o.Locale, o.Key, o.Value, o.UpdatedAt, o.UpdatedBy))
                .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    /// <summary>Sets or replaces the tenant's wording for one key in one locale.</summary>
    public Task SetAsync(string locale, string key, string value, UserId actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxValueLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"An override is at most {MaxValueLength} characters.");
        }

        return db.InTransactionAsync(async ct =>
        {
            ResourceOverride? row = await db.ResourceOverrides
                .FirstOrDefaultAsync(o => o.Locale == locale && o.Key == key, ct).ConfigureAwait(false);
            string? before = row?.Value;
            if (row is null)
            {
                db.ResourceOverrides.Add(row = new ResourceOverride
                {
                    TenantId = tenant.TenantId.Value,
                    Locale = locale,
                    Key = key,
                    Value = value,
                });
            }

            row.Value = value;
            row.UpdatedAt = clock.UtcNow;
            row.UpdatedBy = actor.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Dictionary<string, string> detail = new(StringComparer.Ordinal) { ["locale"] = locale, ["key"] = key, ["value"] = value };
            if (before is not null)
            {
                detail["previous"] = before;
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "wording.override_set",
                ActorId = actor,
                SubjectKind = "wording",
                Detail = detail,
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>Goes back to the shipped wording. False when there was no override.</summary>
    public Task<bool> ClearAsync(string locale, string key, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ResourceOverride? row = await db.ResourceOverrides
                .FirstOrDefaultAsync(o => o.Locale == locale && o.Key == key, ct).ConfigureAwait(false);
            if (row is null)
            {
                return false;
            }

            db.ResourceOverrides.Remove(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "wording.override_cleared",
                ActorId = actor,
                SubjectKind = "wording",
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["locale"] = locale, ["key"] = key, ["previous"] = row.Value },
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}
