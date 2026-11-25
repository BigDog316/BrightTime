using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>
/// What a listing's pictures may be (D105): a main picture and up to four more, PNG or JPEG photos
/// of up to 3 MB, each saying what it shows.
/// </summary>
public static class ListingPictureRules
{
    public const int MaxPictures = 5;
    public const int MaxBytes = 3 * 1024 * 1024;
    public const int MinWidth = 640;
    public const int MinHeight = 360;
    public const int MaxSide = 6000;
    public const int MaxAlt = 250;

    public static readonly ImageRules Image = new(MaxBytes, MinWidth, MinHeight, MaxSide);

    public static Error NotAnImage { get; } =
        Error.Validation("listing_pictures.image", "Choose a PNG or JPEG photo of up to 3 MB, at least 640 by 360 pixels.");

    public static Error TooMany { get; } =
        Error.Conflict("listing_pictures.too_many", $"A listing has up to {MaxPictures} pictures. Remove one before adding another.");

    public static Error NotFound { get; } =
        Error.NotFound("listing_pictures.not_found", "That picture isn't on this listing.");

    /// <summary>The description, trimmed, or the reason it won't do.</summary>
    public static Result<string> Alt(string? text)
    {
        string alt = (text ?? string.Empty).Trim();
        return alt.Length is > 0 and <= MaxAlt
            ? Result<string>.Success(alt)
            : Result<string>.Failure(Error.Validation("listing_pictures.alt", $"Say what the picture shows, in up to {MaxAlt} characters."));
    }
}

/// <summary>
/// A listing's picture as its editor sees it: whether it's the main one, and whether it waits for
/// an organiser with the listing's pending edit (D105).
/// </summary>
public sealed record ListingPictureView(Guid MediaId, string Alt, bool IsMain, bool Waiting);

/// <summary>
/// A listing's pictures (D105). The bytes are a public media object (D103), shown at
/// <c>/media/{id}</c>; what keeps an unsuitable one from families is that nothing shows it until an
/// organiser has approved it with the listing.
/// </summary>
/// <remarks>
/// <para>
/// On a draft, or a listing sent back or taken down, a picture is part of the listing, reviewed
/// when it's sent in. On a published listing a new picture waits with the listing's pending edit
/// (D101), which one is made for it if there's none: approved, it's live; sent back, it waits
/// still; discarded, it goes. Taking one away, saying what one shows, or choosing the main one acts
/// at once, and the last two put the listing among those changed since review (D15).
/// </para>
/// <para>
/// Platform keeps the bytes in its own transaction, so a picture whose listing write fails is
/// removed again, and a picture let go is removed once nothing points to it: a seasonal clone
/// shares its original's pictures.
/// </para>
/// </remarks>
public sealed partial class ListingEditor
{
    /// <summary>A listing's pictures, the main one first and then in the order they were added.</summary>
    public Task<IReadOnlyList<ListingPictureView>> PicturesAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ListingPictureView>>(async ct =>
        {
            Guid? hero = await db.Listings.AsNoTracking().Where(l => l.Id == listingId).Select(l => l.HeroMediaId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return [.. (await PictureRowsAsync(listingId, ct).ConfigureAwait(false))
                .Select(m => new ListingPictureView(m.MediaId, m.AltText, m.MediaId == hero, m.State == ListingMediaRecord.Waiting))
                .OrderBy(p => p.IsMain ? 0 : 1)];
        }, cancellationToken);

    /// <summary>
    /// Adds a picture. Live at once on a listing that isn't published; on a published one it waits
    /// for an organiser with the listing's pending edit.
    /// </summary>
    public async Task<Result> AddPictureAsync(
        OrganisationId organisation, Guid listingId, ReadOnlyMemory<byte> file, string? alt, UserId actor, CancellationToken cancellationToken = default)
    {
        Result<string> said = ListingPictureRules.Alt(alt);
        if (!said.IsSuccess)
        {
            return Result.Failure(said.Error!);
        }

        // Checked before the bytes are kept, so a refused listing stores nothing.
        Result open = await db.InTransactionAsync(async ct =>
        {
            ListingRecord? listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
            return await CanChangePicturesAsync(listing, organisation, adding: true, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return open;
        }

        if (await media.AddImageAsync(file, ListingPictureRules.Image, MediaVisibility.Public, actor, cancellationToken).ConfigureAwait(false) is not { } image)
        {
            return Result.Failure(ListingPictureRules.NotAnImage);
        }

        Result added;
        try
        {
            added = await WithPicturesAsync(organisation, listingId, adding: true, async (listing, pictures, ct) =>
            {
                bool published = listing.Status == ListingStatus.Published.ToDbValue();
                db.ListingMedia.Add(new ListingMediaRecord
                {
                    Id = UuidV7.NewId(),
                    ListingId = listing.Id,
                    MediaId = image.MediaId,
                    AltText = said.Value,
                    SortOrder = (short)(pictures.Count == 0 ? 0 : pictures.Max(p => p.SortOrder) + 1),
                    State = published ? ListingMediaRecord.Waiting : ListingMediaRecord.Live,
                });

                if (published)
                {
                    await HoldForReviewAsync(listing, actor, ct).ConfigureAwait(false);
                }
                else if (listing.HeroMediaId is null)
                {
                    listing.HeroMediaId = image.MediaId;
                }

                return await PicturesChangedAsync(listing, organisation, actor, "listing.picture_added", image.MediaId,
                    published ? ListingMediaRecord.Waiting : ListingMediaRecord.Live, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await media.RemoveAsync(image.MediaId, actor, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!added.IsSuccess)
        {
            await media.RemoveAsync(image.MediaId, actor, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    /// <summary>Changes what a picture shows. A live one's new words are live at once, and the listing counts as changed since review.</summary>
    public Task<Result> DescribePictureAsync(
        OrganisationId organisation, Guid listingId, Guid mediaId, string? alt, UserId actor, CancellationToken cancellationToken = default)
    {
        Result<string> said = ListingPictureRules.Alt(alt);
        if (!said.IsSuccess)
        {
            return Task.FromResult(Result.Failure(said.Error!));
        }

        return WithPicturesAsync(organisation, listingId, adding: false, async (listing, pictures, ct) =>
        {
            if (pictures.FirstOrDefault(p => p.MediaId == mediaId) is not { } picture)
            {
                return Result.Failure(ListingPictureRules.NotFound);
            }

            picture.AltText = said.Value;
            if (picture.State == ListingMediaRecord.Waiting)
            {
                await HoldForReviewAsync(listing, actor, ct).ConfigureAwait(false);
            }
            else
            {
                Touch(listing);
            }

            return await PicturesChangedAsync(listing, organisation, actor, "listing.picture_described", mediaId, picture.State, ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Makes a live picture the main one: on result cards, and first on the listing's page.</summary>
    public Task<Result> MakeMainPictureAsync(
        OrganisationId organisation, Guid listingId, Guid mediaId, UserId actor, CancellationToken cancellationToken = default) =>
        WithPicturesAsync(organisation, listingId, adding: false, async (listing, pictures, ct) =>
        {
            if (pictures.FirstOrDefault(p => p.MediaId == mediaId) is not { } picture)
            {
                return Result.Failure(ListingPictureRules.NotFound);
            }

            if (picture.State != ListingMediaRecord.Live)
            {
                return Result.Failure(Error.Conflict("listing_pictures.waiting", "This picture waits for an organiser. It can be the main one once it's approved."));
            }

            if (listing.HeroMediaId == mediaId)
            {
                return Result.Success();
            }

            listing.HeroMediaId = mediaId;
            Touch(listing);
            return await PicturesChangedAsync(listing, organisation, actor, "listing.picture_main", mediaId, picture.State, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Takes a picture away at once, live or waiting: it can only take something away from what an
    /// organiser approved. The next picture becomes the main one if this was it.
    /// </summary>
    public async Task<Result> RemovePictureAsync(
        OrganisationId organisation, Guid listingId, Guid mediaId, UserId actor, CancellationToken cancellationToken = default)
    {
        Result removed = await WithPicturesAsync(organisation, listingId, adding: false, async (listing, pictures, ct) =>
        {
            if (pictures.FirstOrDefault(p => p.MediaId == mediaId) is not { } picture)
            {
                return Result.Failure(ListingPictureRules.NotFound);
            }

            db.ListingMedia.Remove(picture);
            pictures.Remove(picture);
            if (listing.HeroMediaId == mediaId)
            {
                listing.HeroMediaId = NextMain(pictures);
            }

            // A pending edit that held only pictures, and holds none now, has nothing left to ask.
            if (picture.State == ListingMediaRecord.Waiting
                && !pictures.Any(p => p.State == ListingMediaRecord.Waiting)
                && await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listing.Id, ct).ConfigureAwait(false) is { } pending
                && ListingRevisions.Deserialise(pending.Snapshot) is { } saved
                && ListingRevisions.CompareWording((await ReadFormAsync(listing.Id, ct).ConfigureAwait(false))!.Value.Form, saved).Count == 0)
            {
                db.ListingPendingEdits.Remove(pending);
            }

            return await PicturesChangedAsync(listing, organisation, actor, "listing.picture_removed", mediaId, picture.State, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (removed.IsSuccess)
        {
            await ForgetPicturesAsync([mediaId], actor, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// Runs a change to a listing's pictures in one transaction, with the listing's row locked so
    /// two changes can't both find room for a fifth picture. The listing and its pictures are tracked.
    /// </summary>
    private Task<Result> WithPicturesAsync(
        OrganisationId organisation,
        Guid listingId,
        bool adding,
        Func<ListingRecord, List<ListingMediaRecord>, CancellationToken, Task<Result>> change,
        CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            await db.Database.ExecuteSqlAsync($"select 1 from opportunity.listing where id = {listingId} for update", ct).ConfigureAwait(false);
            ListingRecord? listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
            Result open = await CanChangePicturesAsync(listing, organisation, adding, ct).ConfigureAwait(false);
            if (!open.IsSuccess)
            {
                return open;
            }

            List<ListingMediaRecord> pictures = await db.ListingMedia.Where(m => m.ListingId == listingId)
                .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
            return await change(listing!, pictures, ct).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<Result> CanChangePicturesAsync(ListingRecord? listing, OrganisationId organisation, bool adding, CancellationToken ct)
    {
        if (listing is null || listing.OrgId != organisation.Value)
        {
            return Result.Failure(Error.Forbidden("listing.not_yours", "That listing belongs to another organisation."));
        }

        ListingStatus status = ParseStatus(listing.Status);
        if (!IsEditable(status))
        {
            return Result.Failure(Error.Conflict("listing_pictures.not_editable", "This listing can't be changed now, so neither can its pictures."));
        }

        if (adding && await db.ListingMedia.CountAsync(m => m.ListingId == listing.Id, ct).ConfigureAwait(false) >= ListingPictureRules.MaxPictures)
        {
            return Result.Failure(ListingPictureRules.TooMany);
        }

        return Result.Success();
    }

    /// <summary>
    /// A published listing's picture change waits with its pending edit: one is made from the live
    /// listing if there's none, and one sent back is waiting again.
    /// </summary>
    private async Task HoldForReviewAsync(ListingRecord listing, UserId actor, CancellationToken ct)
    {
        ListingPendingEditRecord? pending = await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listing.Id, ct).ConfigureAwait(false);
        if (pending is null)
        {
            pending = new ListingPendingEditRecord
            {
                ListingId = listing.Id,
                TenantId = listing.TenantId,
                Snapshot = ListingRevisions.Serialise((await ReadFormAsync(listing.Id, ct).ConfigureAwait(false))!.Value.Form),
                State = ListingPendingEditRecord.Waiting,
            };
            db.ListingPendingEdits.Add(pending);
        }

        pending.State = ListingPendingEditRecord.Waiting;
        pending.SavedAt = clock.UtcNow;
        pending.SavedBy = actor.Value;
    }

    /// <summary>A published listing's live change, for the organiser's "changed since review" lane (D15).</summary>
    private void Touch(ListingRecord listing) => listing.UpdatedAt = clock.UtcNow;

    private async Task<Result> PicturesChangedAsync(
        ListingRecord listing, OrganisationId organisation, UserId actor, string action, Guid mediaId, string state, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await db.ScheduleProjectionAsync(tenant.TenantId, listing.Id, ct).ConfigureAwait(false);
        await audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            ActorId = actor,
            ActorOrgId = organisation,
            SubjectKind = "listing",
            SubjectIds = [listing.Id, mediaId],
            Detail = new Dictionary<string, string> { ["state"] = state, ["status"] = listing.Status },
        }, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Task<List<ListingMediaRecord>> PictureRowsAsync(Guid listingId, CancellationToken ct) =>
        db.ListingMedia.AsNoTracking().Where(m => m.ListingId == listingId)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync(ct);

    /// <summary>The first live picture left, to be the main one.</summary>
    private static Guid? NextMain(IEnumerable<ListingMediaRecord> pictures) =>
        pictures.Where(p => p.State == ListingMediaRecord.Live).OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => (Guid?)p.MediaId).FirstOrDefault();

    /// <summary>
    /// Makes a listing's waiting pictures live, in the caller's transaction (an approved edit, or a
    /// listing taken down with its edit), and gives it a main picture if it had none.
    /// </summary>
    private async Task PublishWaitingPicturesAsync(ListingRecord listing, CancellationToken ct)
    {
        List<ListingMediaRecord> pictures = await db.ListingMedia.Where(m => m.ListingId == listing.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (ListingMediaRecord p in pictures.Where(p => p.State == ListingMediaRecord.Waiting))
        {
            p.State = ListingMediaRecord.Live;
        }

        listing.HeroMediaId ??= NextMain(pictures);
    }

    /// <summary>Drops a listing's waiting pictures, in the caller's transaction; the caller forgets their bytes after it commits.</summary>
    private async Task<List<Guid>> DropWaitingPicturesAsync(Guid listingId, CancellationToken ct)
    {
        List<ListingMediaRecord> waiting = await db.ListingMedia
            .Where(m => m.ListingId == listingId && m.State == ListingMediaRecord.Waiting).ToListAsync(ct).ConfigureAwait(false);
        db.ListingMedia.RemoveRange(waiting);
        return [.. waiting.Select(m => m.MediaId)];
    }

    /// <summary>The waiting pictures of a published listing's pending edit, for its review.</summary>
    private async Task<IReadOnlyList<ListingPicture>> WaitingPicturesAsync(Guid listingId, CancellationToken ct) =>
        [.. (await PictureRowsAsync(listingId, ct).ConfigureAwait(false))
            .Where(m => m.State == ListingMediaRecord.Waiting)
            .Select(m => new ListingPicture(m.MediaId, m.AltText))];

    /// <summary>A seasonal clone's pictures: the original's live ones, sharing their bytes.</summary>
    private async Task CopyPicturesAsync(Guid from, Guid to, CancellationToken ct) =>
        db.ListingMedia.AddRange((await PictureRowsAsync(from, ct).ConfigureAwait(false))
            .Where(m => m.State == ListingMediaRecord.Live)
            .Select(m => new ListingMediaRecord { Id = UuidV7.NewId(), ListingId = to, MediaId = m.MediaId, AltText = m.AltText, SortOrder = m.SortOrder }));

    /// <summary>Removes the bytes of pictures that nothing points to any more; a clone's shared ones stay.</summary>
    private async Task ForgetPicturesAsync(List<Guid> mediaIds, UserId actor, CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        List<Guid> used = await db.InTransactionAsync(async ct =>
            await db.ListingMedia.AsNoTracking().Where(m => mediaIds.Contains(m.MediaId)).Select(m => m.MediaId)
                .Union(db.Listings.AsNoTracking().Where(l => l.HeroMediaId != null && mediaIds.Contains(l.HeroMediaId.Value)).Select(l => l.HeroMediaId!.Value))
                .ToListAsync(ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        foreach (Guid id in mediaIds.Except(used))
        {
            await media.RemoveAsync(id, actor, cancellationToken).ConfigureAwait(false);
        }
    }
}
