using System.Globalization;
using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Media;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Media;

/// <summary>
/// The tenant's media (D103). An image is read by <see cref="ImageCleaner"/>, so only a whole PNG
/// or JPEG gets in, with its metadata gone, and it's held to the feature's rules; then its bytes
/// and its <c>media_object</c> row are written in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Those checks are what makes an image <c>clean</c> for now: the type read from the bytes, the
/// sizes, the metadata stripped, SVG and everything else refused. Virus scanning and re-encoding
/// are M5's media safety; until then only adults' uploads use this (curation, D103), and
/// <c>scan_detail</c> says how far an object was checked.
/// </para>
/// <para>
/// A public object is fetchable by its id from the moment it's added, before whatever shows it is
/// published; ids are UUIDv7, so one can't be guessed from another.
/// </para>
/// </remarks>
public sealed class MediaLibrary(PlatformDbContext db, IObjectStore store, ITenantContext tenant, IClock clock, IAuditWriter audit) : IMediaLibrary
{
    /// <summary>What <c>scan_detail</c> says of an image checked here.</summary>
    public const string CheckedDetail = "checked: type, size and metadata stripped (D103); not virus-scanned";

    private static readonly string[] Visibilities = [MediaVisibility.Private, MediaVisibility.Organisation, MediaVisibility.Tenant, MediaVisibility.Public];

    public async Task<StoredImage?> AddImageAsync(
        ReadOnlyMemory<byte> file, ImageRules rules, string visibility, UserId uploadedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Visibilities.Contains(visibility))
        {
            throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Not a ck_media_visibility value.");
        }

        if (ImageCleaner.Clean(file.Span, rules.MaxBytes) is not { } clean
            || clean.Width < rules.MinWidth || clean.Height < rules.MinHeight
            || Math.Max(clean.Width, clean.Height) > rules.MaxSide)
        {
            return null;
        }

        Guid id = UuidV7.NewId();
        string key = "images/" + id.ToString("N") + (clean.ContentType == "image/png" ? ".png" : ".jpg");
        return await db.InTransactionAsync(async ct =>
        {
            StoredObject stored = await store.PutAsync(key, clean.Bytes, clean.ContentType, ct).ConfigureAwait(false);
            db.MediaObjects.Add(new MediaObject
            {
                Id = id,
                TenantId = tenant.TenantId.Value,
                ObjectKey = key,
                ContentType = clean.ContentType,
                ByteSize = stored.ByteSize,
                Sha256 = stored.Sha256,
                Width = clean.Width,
                Height = clean.Height,
                Origin = "upload",
                ScanState = "clean",
                ScanDetail = CheckedDetail,
                ExifStrippedAt = clock.UtcNow,
                UploadedBy = uploadedBy.Value,
                Visibility = visibility,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "media.added",
                ActorId = uploadedBy,
                SubjectKind = "media",
                SubjectIds = [id],
                Detail = new Dictionary<string, string>
                {
                    ["type"] = clean.ContentType,
                    ["bytes"] = stored.ByteSize.ToString(CultureInfo.InvariantCulture),
                    ["visibility"] = visibility,
                },
            }, ct).ConfigureAwait(false);
            return new StoredImage(id, clean.Width, clean.Height);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<MediaFile?> GetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<MediaFile?>(async ct =>
        {
            var media = await db.MediaObjects.AsNoTracking()
                .Where(m => m.Id == mediaId && m.ScanState == "clean" && m.Visibility == MediaVisibility.Public)
                .Select(m => new { m.ObjectKey, m.ContentType, m.Sha256 })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (media is null || await store.ReadAsync(media.ObjectKey, ct).ConfigureAwait(false) is not { } bytes)
            {
                return null;
            }

            return new MediaFile(media.ContentType, bytes, "\"" + Convert.ToHexStringLower(media.Sha256) + "\"");
        }, cancellationToken);

    public Task<bool> RemoveAsync(Guid mediaId, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            MediaObject? media = await db.MediaObjects.FirstOrDefaultAsync(m => m.Id == mediaId, ct).ConfigureAwait(false);
            if (media is null)
            {
                return false;
            }

            db.MediaObjects.Remove(media);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await store.DeleteAsync(media.ObjectKey, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "media.removed",
                ActorId = actor,
                SubjectKind = "media",
                SubjectIds = [mediaId],
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}
