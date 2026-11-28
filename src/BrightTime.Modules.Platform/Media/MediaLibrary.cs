using System.Globalization;
using System.Security.Cryptography;
using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Media;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Platform.Media;

/// <summary>
/// The tenant's media (D103). An image is read by <see cref="ImageCleaner"/>, so only a whole PNG
/// or JPEG gets in, and it's held to the feature's rules; the upload as it came is virus-scanned
/// (D123), and refused if the scan finds something or can't be done; then it's decoded and made
/// again from its pixels by <see cref="ImageSizer"/>, so only a picture gets in and none of the
/// uploaded file's bytes are kept, and a public one gets its smaller copies (D106). Its bytes, its
/// copies' and its <c>media_object</c> row are written in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Those checks are what makes an image <c>clean</c>: the type read from the bytes, the sizes, the
/// scan, the decode, and the re-encoding, which leaves no metadata, no orientation and nothing
/// hidden after the picture. SVG and everything else is refused. <c>scan_detail</c> says how an
/// object was checked, and <c>scanned_at</c> when it was virus-scanned: one kept before D123 is
/// scanned by the jobs worker (<see cref="ScanStoredAsync"/>), but not made again, since an
/// object's bytes never change.
/// </para>
/// <para>
/// A public object is fetchable by its id from the moment it's added, before whatever shows it is
/// published; ids are UUIDv7, so one can't be guessed from another.
/// </para>
/// </remarks>
public sealed partial class MediaLibrary(
    PlatformDbContext db, IObjectStore store, ITenantContext tenant, IClock clock, IAuditWriter audit, IVirusScanner scanner, ILogger<MediaLibrary> logger) : IMediaLibrary
{
    /// <summary>What <c>scan_detail</c> said of an image kept before D123.</summary>
    public const string CheckedDetail = "checked: type, size and metadata stripped (D103); not virus-scanned";

    /// <summary>The audit action of a file the virus scan found something in.</summary>
    public const string InfectedAction = "media.infected";

    /// <summary>What <c>scan_detail</c> says of an image kept since D123.</summary>
    public static string KeptDetail(string engine) => $"checked: type and size; virus-scanned by {engine}; decoded and re-encoded (D123)";

    /// <summary>What <c>scan_detail</c> says of an image kept before D123 once the jobs worker has scanned it.</summary>
    public static string ScannedLaterDetail(string engine) => $"checked: type, size and metadata stripped (D103); virus-scanned by {engine} (D123); not re-encoded";

    private static readonly string[] Visibilities = [MediaVisibility.Private, MediaVisibility.Organisation, MediaVisibility.Tenant, MediaVisibility.Public];

    public async Task<Result<StoredImage>> AddImageAsync(
        ReadOnlyMemory<byte> file, ImageRules rules, string visibility, UserId uploadedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Visibilities.Contains(visibility))
        {
            throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Not a ck_media_visibility value.");
        }

        if (ImageCleaner.Clean(file.Span, rules.MaxBytes) is not { } clean
            || clean.Width < rules.MinWidth || clean.Height < rules.MinHeight
            || Math.Max(clean.Width, clean.Height) > rules.MaxSide
            || (rules.MaxAspect > 0 && Math.Max(clean.Width, clean.Height) > rules.MaxAspect * Math.Min(clean.Width, clean.Height)))
        {
            return MediaRefusals.NotAPicture;
        }

        // The upload as it came, which is what a scanner's signatures are written for.
        ScanResult scan = await scanner.ScanAsync(file, cancellationToken).ConfigureAwait(false);
        if (scan.Verdict == ScanVerdict.Infected)
        {
            await RecordInfectedAsync(file, scan, uploadedBy, cancellationToken).ConfigureAwait(false);
            return MediaRefusals.Infected;
        }

        if (scan.Verdict != ScanVerdict.Clean)
        {
            return MediaRefusals.Unchecked;
        }

        bool isPublic = visibility == MediaVisibility.Public;
        if (await ImageSizer.ReencodeAsync(clean.Bytes, clean.ContentType, clean.Width, clean.Height, isPublic ? MediaSizes.All : [], cancellationToken).ConfigureAwait(false)
            is not { } image)
        {
            return MediaRefusals.NotAPicture;
        }

        Guid id = UuidV7.NewId();
        string key = "images/" + id.ToString("N") + (image.ContentType == "image/png" ? ".png" : ".jpg");
        IReadOnlyList<ImageCopy> copies = image.Copies;
        return await db.InTransactionAsync<Result<StoredImage>>(async ct =>
        {
            StoredObject stored = await store.PutAsync(key, image.Bytes, image.ContentType, ct).ConfigureAwait(false);
            db.MediaObjects.Add(new MediaObject
            {
                Id = id,
                TenantId = tenant.TenantId.Value,
                ObjectKey = key,
                ContentType = image.ContentType,
                ByteSize = stored.ByteSize,
                Sha256 = stored.Sha256,
                Width = image.Width,
                Height = image.Height,
                Origin = "upload",
                ScanState = "clean",
                ScanDetail = KeptDetail(scan.Engine),
                ScannedAt = clock.UtcNow,
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
                    ["type"] = image.ContentType,
                    ["bytes"] = stored.ByteSize.ToString(CultureInfo.InvariantCulture),
                    ["visibility"] = visibility,
                    ["sizes"] = string.Join(',', copies.Select(c => c.Size.ToString(CultureInfo.InvariantCulture))),
                    ["scan"] = scan.Engine,
                },
            }, ct).ConfigureAwait(false);
            return new StoredImage(id, image.Width, image.Height);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Virus-scans an image kept before D123, once, for the jobs worker. One the scan finds
    /// something in is <c>infected</c> from then on, so it's served nowhere, and audited; one the
    /// scan couldn't be done for is left as it was, to be tried again.
    /// </summary>
    public async Task<ScanOutcome> ScanStoredAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await db.InTransactionAsync(async ct =>
        {
            string? key = await db.MediaObjects.AsNoTracking()
                .Where(m => m.Id == mediaId && m.ScanState == "clean" && m.ScannedAt == null)
                .Select(m => m.ObjectKey)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return key is null ? null : await store.ReadAsync(key, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return ScanOutcome.Gone;
        }

        // Scanned outside a transaction: a connection isn't held while clamd works.
        ScanResult scan = await scanner.ScanAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (scan.Verdict == ScanVerdict.Failed)
        {
            return ScanOutcome.Unchecked;
        }

        return await db.InTransactionAsync(async ct =>
        {
            MediaObject? media = await db.MediaObjects
                .FromSql($"select * from platform.media_object where id = {mediaId} and scan_state = 'clean' and scanned_at is null for update skip locked")
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (media is null)
            {
                return ScanOutcome.Gone;
            }

            media.ScannedAt = clock.UtcNow;
            if (scan.Verdict == ScanVerdict.Clean)
            {
                media.ScanDetail = ScannedLaterDetail(scan.Engine);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return ScanOutcome.Clean;
            }

            media.ScanState = "infected";
            media.ScanDetail = $"infected: {scan.Detail} (virus-scanned by {scan.Engine}, D123)";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = InfectedAction,
                Outcome = AuditOutcome.Deny,
                Reason = "virus scan",
                SubjectKind = "media",
                SubjectIds = [mediaId],
                Detail = InfectedDetail(bytes, scan),
            }, ct).ConfigureAwait(false);
            LogInfected(logger, scan.Detail ?? "", mediaId);
            return ScanOutcome.Infected;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Up to <paramref name="count"/> images kept before D123 and not yet virus-scanned, oldest first.</summary>
    public Task<List<Guid>> UnscannedAsync(int count, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => db.MediaObjects.AsNoTracking()
            .Where(m => m.ScanState == "clean" && m.ScannedAt == null)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .Take(count)
            .ToListAsync(ct), cancellationToken);

    /// <summary>A refused upload's trace: who sent it, what the scan found, and the file's hash, but not the file.</summary>
    private Task<bool> RecordInfectedAsync(ReadOnlyMemory<byte> file, ScanResult scan, UserId uploadedBy, CancellationToken cancellationToken)
    {
        LogRefused(logger, scan.Detail ?? "", uploadedBy.Value);
        Dictionary<string, string> detail = InfectedDetail(file.ToArray(), scan);
        AuditAccess refused = new()
        {
            Action = InfectedAction,
            Outcome = AuditOutcome.Deny,
            Reason = "virus scan",
            ActorId = uploadedBy,
            SubjectKind = "media",
            Detail = detail,
        };
        return db.InTransactionAsync(async ct =>
        {
            await audit.RecordInTransactionAsync(db, refused, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static Dictionary<string, string> InfectedDetail(byte[] file, ScanResult scan) => new()
    {
        ["engine"] = scan.Engine,
        ["signature"] = scan.Detail ?? "",
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(file)),
        ["bytes"] = file.Length.ToString(CultureInfo.InvariantCulture),
    };

    [LoggerMessage(EventId = 6111, Level = LogLevel.Warning, Message = "The virus scan found {Signature} in an upload by {UserId}; it wasn't kept.")]
    private static partial void LogRefused(ILogger logger, string signature, Guid userId);

    [LoggerMessage(EventId = 6112, Level = LogLevel.Error, Message = "The virus scan found {Signature} in stored image {MediaId}; it's infected now and served nowhere.")]
    private static partial void LogInfected(ILogger logger, string signature, Guid mediaId);

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

    public async Task<MediaFile?> GetPrintCopyAsync(Guid mediaId, int longest, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(longest);
        var image = await db.InTransactionAsync(async ct =>
        {
            var media = await db.MediaObjects.AsNoTracking()
                .Where(m => m.Id == mediaId && m.ScanState == "clean" && m.Width != null && m.Height != null)
                .Select(m => new { m.ObjectKey, Width = m.Width!.Value, Height = m.Height!.Value })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return media is null || await store.ReadAsync(media.ObjectKey, ct).ConfigureAwait(false) is not { } bytes
                ? null
                : new { Bytes = bytes, media.Width, media.Height };
        }, cancellationToken).ConfigureAwait(false);

        // Decoded outside the transaction: a connection isn't held while Skia works.
        if (image is null || await ImageSizer.PrintCopyAsync(image.Bytes, image.Width, image.Height, longest, cancellationToken).ConfigureAwait(false) is not { } jpeg)
        {
            return null;
        }

        return new MediaFile("image/jpeg", jpeg, "\"" + Convert.ToHexStringLower(SHA256.HashData(jpeg)) + "\"");
    }

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

/// <summary>What virus-scanning a stored image came to (D123).</summary>
public enum ScanOutcome
{
    /// <summary>Nothing found; it's marked scanned.</summary>
    Clean,

    /// <summary>The scan found something: it's infected now, and served nowhere.</summary>
    Infected,

    /// <summary>The scan couldn't be done; it's left to be tried again.</summary>
    Unchecked,

    /// <summary>Already scanned, removed, not clean, or being scanned by another worker: nothing to do.</summary>
    Gone,
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
