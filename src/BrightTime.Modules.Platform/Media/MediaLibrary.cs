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
/// or JPEG gets in, with its metadata gone, and it's held to the feature's rules; it's decoded by
/// <see cref="ImageSizer"/>, so only one that is a picture does, and a public one gets its smaller
/// copies (D106). Then its bytes, its copies' and its <c>media_object</c> row are written in one
/// transaction.
/// </summary>
/// <remarks>
/// <para>
/// Those checks are what makes an image <c>clean</c> for now: the type read from the bytes, the
/// sizes, the metadata stripped, SVG and everything else refused. Virus scanning and re-encoding
/// are M5's media safety. Until then a young person's pictures (D104) are kept private and shown
/// beyond them and the staff reviewing them only once staff approve the project; <c>scan_detail</c>
/// says how far an object was checked.
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

        bool isPublic = visibility == MediaVisibility.Public;
        if (await ImageSizer.CopiesAsync(clean.Bytes, clean.Width, clean.Height, isPublic ? MediaSizes.All : [], cancellationToken).ConfigureAwait(false)
            is not { } copies)
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
                SizedAt = isPublic ? clock.UtcNow : null,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await KeepCopiesAsync(id, copies, ct).ConfigureAwait(false);
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
                    ["sizes"] = string.Join(',', copies.Select(c => c.Size.ToString(CultureInfo.InvariantCulture))),
                },
            }, ct).ConfigureAwait(false);
            return new StoredImage(id, clean.Width, clean.Height);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<MediaFile?> GetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
        FileAsync(mediaId, publicOnly: true, cancellationToken);

    public Task<SizedMedia?> GetPublicSizeAsync(Guid mediaId, int size, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<SizedMedia?>(async ct =>
        {
            var media = await db.MediaObjects.AsNoTracking()
                .Where(m => m.Id == mediaId && m.ScanState == "clean" && m.Visibility == MediaVisibility.Public)
                .Select(m => new { m.SizedAt })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (media is null)
            {
                return null;
            }

            var copy = await db.MediaVariants.AsNoTracking()
                .Where(v => v.MediaId == mediaId && v.Size == size)
                .Select(v => new { v.ObjectKey, v.ContentType, v.Sha256 })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (copy is null || await store.ReadAsync(copy.ObjectKey, ct).ConfigureAwait(false) is not { } bytes)
            {
                return new SizedMedia(null, Settled: media.SizedAt is not null && copy is null);
            }

            return new SizedMedia(new MediaFile(copy.ContentType, bytes, "\"" + Convert.ToHexStringLower(copy.Sha256) + "\""), Settled: true);
        }, cancellationToken);

    public Task<MediaFile?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
        FileAsync(mediaId, publicOnly: false, cancellationToken);

    private Task<MediaFile?> FileAsync(Guid mediaId, bool publicOnly, CancellationToken cancellationToken) =>
        db.InTransactionAsync<MediaFile?>(async ct =>
        {
            var media = await db.MediaObjects.AsNoTracking()
                .Where(m => m.Id == mediaId && m.ScanState == "clean" && (!publicOnly || m.Visibility == MediaVisibility.Public))
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

            // The copies' rows go with the object; their bytes are blobs of their own.
            List<string> copies = await db.MediaVariants.Where(v => v.MediaId == mediaId).Select(v => v.ObjectKey).ToListAsync(ct).ConfigureAwait(false);
            db.MediaObjects.Remove(media);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await store.DeleteAsync(media.ObjectKey, ct).ConfigureAwait(false);
            foreach (string copy in copies)
            {
                await store.DeleteAsync(copy, ct).ConfigureAwait(false);
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "media.removed",
                ActorId = actor,
                SubjectKind = "media",
                SubjectIds = [mediaId],
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Makes a public image's smaller copies, if they haven't been: for one added before D106, by
    /// the jobs worker. An image that doesn't decode is marked sized all the same, with no copies,
    /// so it's tried once; the original stands in, as it did.
    /// </summary>
    public Task<SizingOutcome> MakeSizesAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            MediaObject? media = await db.MediaObjects
                .FromSql($"select * from platform.media_object where id = {mediaId} and sized_at is null and visibility = 'public' and scan_state = 'clean' for update skip locked")
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (media is null)
            {
                return SizingOutcome.Gone;
            }

            IReadOnlyList<ImageCopy>? copies = media.Width is { } width && media.Height is { } height
                && await store.ReadAsync(media.ObjectKey, ct).ConfigureAwait(false) is { } bytes
                    ? await ImageSizer.CopiesAsync(bytes, width, height, MediaSizes.All, ct).ConfigureAwait(false)
                    : null;
            media.SizedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (copies is null)
            {
                return SizingOutcome.Undecodable;
            }

            await KeepCopiesAsync(media.Id, copies, ct).ConfigureAwait(false);
            return SizingOutcome.Made;
        }, cancellationToken);

    /// <summary>Up to <paramref name="count"/> public images still waiting for their copies, oldest first.</summary>
    public Task<List<Guid>> UnsizedAsync(int count, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => db.MediaObjects.AsNoTracking()
            .Where(m => m.Visibility == MediaVisibility.Public && m.ScanState == "clean" && m.SizedAt == null
                        && (m.ContentType == "image/png" || m.ContentType == "image/jpeg"))
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .Take(count)
            .ToListAsync(ct), cancellationToken);

    private async Task KeepCopiesAsync(Guid mediaId, IReadOnlyList<ImageCopy> copies, CancellationToken ct)
    {
        foreach (ImageCopy copy in copies)
        {
            string key = "images/" + mediaId.ToString("N") + "-" + copy.Size.ToString(CultureInfo.InvariantCulture) + ".webp";
            StoredObject stored = await store.PutAsync(key, copy.Bytes, ImageSizer.ContentType, ct).ConfigureAwait(false);
            db.MediaVariants.Add(new MediaVariant
            {
                TenantId = tenant.TenantId.Value,
                MediaId = mediaId,
                Size = copy.Size,
                ObjectKey = key,
                ContentType = ImageSizer.ContentType,
                ByteSize = stored.ByteSize,
                Sha256 = stored.Sha256,
                Width = copy.Width,
                Height = copy.Height,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>What making an image's copies came to.</summary>
public enum SizingOutcome
{
    /// <summary>Made: one for each size it's wider than, perhaps none.</summary>
    Made,

    /// <summary>It didn't decode, so it has none; it isn't tried again.</summary>
    Undecodable,

    /// <summary>Already sized, removed, not public, or being sized by another worker: nothing to do.</summary>
    Gone,
}
