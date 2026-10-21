using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Attendance.Devices;

public sealed record DeviceView(
    Guid Id,
    string DeviceId,
    string Label,
    Guid? SiteId,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string? AppVersion,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    bool WipeRequested);

/// <summary>
/// What a device is told each time it reaches the server: whether to wipe its offline data now,
/// and how long it may stay offline before it must refuse to open (docs/plan/07, T12).
/// </summary>
public sealed record DeviceContact(bool Registered, bool Wipe, int MaxOfflineHours);

/// <summary>
/// The phones and kiosks an organisation lets hold an offline roster and sync attendance
/// (M3b, D51).
/// </summary>
/// <remarks>
/// <para>
/// A device is registered to one organisation by one of its staff. Offline sync accepts batches
/// only from a registered, unrevoked device (<see cref="RequireActiveAsync"/>), so a lost phone,
/// once revoked, can neither add records nor, through idempotent replay, change any.
/// </para>
/// <para>
/// Revoking asks for a wipe. The device learns it on its next contact (<see cref="ContactAsync"/>),
/// which needs no sign-in: a phone whose user has left can still be told to purge, and it refuses
/// to open after <see cref="MaxOfflineHours"/> hours without contact anyway. A revoked device id is
/// never registered again; a wiped device makes a fresh one.
/// </para>
/// </remarks>
public sealed class KioskDevices(AttendanceDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, IDirectoryQueries directory)
{
    public const int MaxOfflineHours = 72;

    public const string Registered = "attendance.device_registered";
    public const string Revoked = "attendance.device_revoked";

    public Task<IReadOnlyList<DeviceView>> ListAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<DeviceView>>(async ct =>
            [.. (await db.Devices.AsNoTracking().Where(d => d.OrgId == org.Value).OrderBy(d => d.RevokedAt != null).ThenBy(d => d.Label).ToListAsync(ct).ConfigureAwait(false))
                .Select(View)], cancellationToken);

    /// <summary>Registers this device to the organisation; registering it again refreshes its label and version.</summary>
    public Task<Result<(DeviceView View, bool Created)>> RegisterAsync(OrganisationId org, UserId actor, string deviceId, string label, Guid? siteId, string? appVersion, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? device = deviceId?.Trim();
            if (device is not { Length: >= 8 and <= 100 } || device.Any(char.IsControl))
            {
                return Result<(DeviceView, bool)>.Failure(Error.Validation("device.id", "A device id of 8 to 100 characters."));
            }

            string? name = label?.Trim();
            if (name is not { Length: > 0 and <= 80 } || name.Any(char.IsControl))
            {
                return Result<(DeviceView, bool)>.Failure(Error.Validation("device.label", "A label of 1 to 80 characters, such as Front desk tablet."));
            }

            if (siteId is { } site && !(await directory.GetOrganisationSitesAsync(org, ct).ConfigureAwait(false)).Any(s => s.SiteId == site))
            {
                return Result<(DeviceView, bool)>.Failure(Error.Validation("device.site", "One of your organisation's sites, or none."));
            }

            DateTimeOffset now = clock.StoredNow();
            string? version = appVersion is { Length: <= 40 } ? appVersion : null;
            if (await db.Devices.FirstOrDefaultAsync(d => d.TenantId == tenant.TenantId.Value && d.DeviceId == device, ct).ConfigureAwait(false) is { } known)
            {
                if (known.OrgId != org.Value)
                {
                    return Result<(DeviceView, bool)>.Failure(Error.Conflict("device.taken", "This device belongs to another organisation."));
                }

                if (known.RevokedAt is not null)
                {
                    return Result<(DeviceView, bool)>.Failure(Error.Conflict("device.revoked", "This device has been revoked. Wipe it, and it will register afresh."));
                }

                known.Label = name;
                known.SiteId = siteId;
                known.AppVersion = version ?? known.AppVersion;
                known.LastSeenAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<(DeviceView, bool)>.Success((View(known), false));
            }

            KioskDeviceRecord record = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                SiteId = siteId,
                DeviceId = device,
                Label = name,
                EnrolledBy = actor.Value,
                EnrolledAt = now,
                LastSeenAt = now,
                AppVersion = version,
            };
            db.Devices.Add(record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            audit.Record(new AuditAccess { Action = Registered, SubjectKind = "kiosk_device", SubjectIds = [record.Id], ActorOrgId = org });
            return Result<(DeviceView, bool)>.Success((View(record), true));
        }, cancellationToken);

    /// <summary>
    /// Revokes a device and asks it to wipe. An admin may revoke any of the organisation's devices;
    /// a staff member, the ones they registered (a lost phone is reported by whoever lost it).
    /// </summary>
    public Task<Result<DeviceView>> RevokeAsync(OrganisationId org, UserId actor, bool isAdmin, Guid id, string reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? why = reason?.Trim();
            if (why is not { Length: > 0 and <= 200 })
            {
                return Result<DeviceView>.Failure(Error.Validation("device.reason", "Say why, in up to 200 characters."));
            }

            KioskDeviceRecord? device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.OrgId == org.Value, ct).ConfigureAwait(false);
            if (device is null || (!isAdmin && device.EnrolledBy != actor.Value))
            {
                return Result<DeviceView>.Failure(Error.NotFound("device.not_found", "Your organisation has no device of yours with that id."));
            }

            if (device.RevokedAt is null)
            {
                DateTimeOffset now = clock.StoredNow();
                device.RevokedAt = now;
                device.RevokedReason = why;
                device.WipeRequestedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                audit.Record(new AuditAccess
                {
                    Action = Revoked,
                    SubjectKind = "kiosk_device",
                    SubjectIds = [device.Id],
                    ActorOrgId = org,
                    Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = why },
                });
            }

            return Result<DeviceView>.Success(View(device));
        }, cancellationToken);

    /// <summary>
    /// A device reaching the server. Says whether to wipe; a signed-in member of the device's
    /// organisation also refreshes its last-seen time and version.
    /// </summary>
    public Task<DeviceContact> ContactAsync(string deviceId, string? appVersion, IReadOnlyCollection<Guid> memberOf, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(memberOf);
            string device = deviceId?.Trim() ?? "";
            KioskDeviceRecord? known = device.Length == 0 ? null
                : await db.Devices.FirstOrDefaultAsync(d => d.TenantId == tenant.TenantId.Value && d.DeviceId == device, ct).ConfigureAwait(false);
            if (known is null)
            {
                return new DeviceContact(false, false, MaxOfflineHours);
            }

            if (known.RevokedAt is null && memberOf.Contains(known.OrgId))
            {
                known.LastSeenAt = clock.StoredNow();
                known.AppVersion = appVersion is { Length: <= 40 } ? appVersion : known.AppVersion;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return new DeviceContact(true, known.WipeRequestedAt is not null, MaxOfflineHours);
        }, cancellationToken);

    /// <summary>The device is registered to the organisation and not revoked; refreshes its last-seen time.</summary>
    public async Task<Error?> RequireActiveAsync(OrganisationId org, string deviceId, CancellationToken cancellationToken = default)
    {
        KioskDeviceRecord? known = await db.Devices.FirstOrDefaultAsync(d => d.TenantId == tenant.TenantId.Value && d.DeviceId == deviceId, cancellationToken).ConfigureAwait(false);
        if (known is null || known.OrgId != org.Value)
        {
            return Error.Forbidden("device.unknown", "Register this device with your organisation before it syncs.");
        }

        if (known.RevokedAt is not null)
        {
            return Error.Forbidden("device.revoked", "This device has been revoked. Wipe it, and it will register afresh.");
        }

        known.LastSeenAt = clock.StoredNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static DeviceView View(KioskDeviceRecord d) =>
        new(d.Id, d.DeviceId, d.Label, d.SiteId, d.EnrolledAt, d.LastSeenAt, d.AppVersion, d.RevokedAt, d.RevokedReason, d.WipeRequestedAt is not null);
}
