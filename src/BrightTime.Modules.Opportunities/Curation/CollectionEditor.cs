using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BrightTime.Modules.Opportunities.Curation;

/// <summary>What the organiser writes about a collection. The link name is kept once it has been published.</summary>
public sealed record CollectionInput(string Title, string? Description, string Slug, DateOnly? StartsOn, DateOnly? EndsOn, bool IsFeatured, short SortOrder);

/// <summary>A collection's title and description in one of the tenant's other languages.</summary>
public sealed record CollectionWording(string Locale, string Title, string? Description);

/// <summary>A collection in the organiser's list, whatever its state. <c>EverPublished</c>: its link name is fixed.</summary>
public sealed record CuratedRow(
    Guid Id, string Slug, string Title, string Status, bool IsFeatured, short SortOrder, DateOnly? StartsOn, DateOnly? EndsOn, int Listings, bool EverPublished);

/// <summary>
/// One listing in a collection, in order. <c>Published</c> is false for a listing that has since
/// left the site: the public page leaves it out (the finder shows only published listings), and
/// the organiser sees it marked.
/// </summary>
public sealed record CuratedEntry(Guid ListingId, string Title, string ListingSlug, bool Published, string? Note);

/// <summary>A collection as the organiser edits it.</summary>
public sealed record CollectionDetail(
    Guid Id, string Slug, string Title, string? Description, string Status, DateOnly? StartsOn, DateOnly? EndsOn, bool IsFeatured, short SortOrder,
    bool EverPublished, IReadOnlyList<CollectionWording> Wordings, IReadOnlyList<CuratedEntry> Entries);

/// <summary>A published listing the organiser can add.</summary>
public sealed record ListingChoice(Guid Id, string Title, string Slug);

/// <summary>
/// The organiser's curation (D94): collections of published listings, written, translated,
/// ordered, featured on the home page, given dates, published and archived.
/// </summary>
/// <remarks>
/// <para>
/// The public side is <see cref="CurationQueries"/>: it shows a collection only while it's
/// published and in its dates, and fetches its listings through the finder, which shows only
/// published ones. So a collection never leaks a draft, however it was curated; only published
/// listings can be added here, and one that leaves the site afterwards is marked for the organiser.
/// </para>
/// <para>
/// The link name is the collection's address. It can change until the collection is first
/// published; after that it's kept, because people have shared it. Nothing takes a collection back
/// to draft (archived ones are published again), so a draft has never been published. A published collection
/// otherwise changes in place: there's nothing in it about a young person, and organisers own it.
/// Every change is audited.
/// </para>
/// </remarks>
public sealed partial class CollectionEditor(OpportunitiesDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public const int MaxTitle = 120;
    public const int MaxDescription = 600;
    public const int MaxNote = 300;
    public const int MaxEntries = 60;
    public const int MaxSortOrder = 999;

    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    /// <summary>Untranslated collection text is the organiser's, in English (as <see cref="CurationQueries"/> reads it).</summary>
    public const string SourceLanguage = "en";

    /// <summary>Every collection, in the order the public pages use, drafts and archived ones too.</summary>
    public Task<IReadOnlyList<CuratedRow>> ListAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<CuratedRow>>(async ct =>
        {
            var rows = await db.Collections.AsNoTracking()
                .OrderBy(c => c.Status == Archived).ThenBy(c => c.SortOrder).ThenBy(c => c.Title)
                .Select(c => new { c, Count = db.CollectionItems.Count(i => i.CollectionId == c.Id) })
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. rows.Select(r => new CuratedRow(r.c.Id, r.c.Slug, r.c.Title, r.c.Status, r.c.IsFeatured, r.c.SortOrder, r.c.StartsOn, r.c.EndsOn, r.Count,
                r.c.Status != Draft))];
        }, cancellationToken);

    public Task<CollectionDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            CollectionRecord? c = await db.Collections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            return c is null ? null : await ViewAsync(c, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Begins a collection as a draft.</summary>
    public Task<Result<Guid>> CreateAsync(CollectionInput input, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Check(input) is { } invalid)
            {
                return Result<Guid>.Failure(invalid);
            }

            DateTimeOffset now = clock.StoredNow();
            CollectionRecord c = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                Slug = Slug(input.Slug),
                Title = input.Title.Trim(),
                CuratorUserId = actor.Value,
                CreatedAt = now,
            };
            Apply(c, input, now);
            db.Collections.Add(c);
            if (await SaveAsync(ct).ConfigureAwait(false) is { } taken)
            {
                return Result<Guid>.Failure(taken);
            }

            await AuditAsync("curation.collection_created", c, actor, ct).ConfigureAwait(false);
            return Result<Guid>.Success(c.Id);
        }, cancellationToken);

    /// <summary>Changes what the collection says, its dates, its place and whether it's featured.</summary>
    public Task<Result> UpdateAsync(Guid id, CollectionInput input, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(id, async (c, ct) =>
        {
            if (Check(input) is { } invalid)
            {
                return Result.Failure(invalid);
            }

            string slug = Slug(input.Slug);
            if (!string.Equals(slug, c.Slug, StringComparison.OrdinalIgnoreCase))
            {
                if (c.Status != Draft)
                {
                    return Result.Failure(Error.Conflict("collections.slug_fixed", "The link name stays once a collection has been published."));
                }

                c.Slug = slug;
            }

            Apply(c, input, clock.StoredNow());
            if (await SaveAsync(ct).ConfigureAwait(false) is { } taken)
            {
                return Result.Failure(taken);
            }

            await AuditAsync("curation.collection_changed", c, actor, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Sets the collection's wording in another language; an empty title takes that language's wording away.</summary>
    public Task<Result> WordAsync(Guid id, CollectionWording wording, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(id, async (c, ct) =>
        {
            ArgumentNullException.ThrowIfNull(wording);
            if (!Locale.TryFromCode(wording.Locale, out Locale locale) || locale.Code == SourceLanguage)
            {
                return Result.Failure(Error.Validation("collections.locale", "One of the site's other languages."));
            }

            string title = wording.Title.Trim();
            string? description = Optional(wording.Description);
            if (title.Length > MaxTitle || description is { Length: > MaxDescription })
            {
                return Result.Failure(Error.Validation("collections.text", $"A title of up to {MaxTitle} characters and a description of up to {MaxDescription}."));
            }

            CollectionTranslationRecord? existing = await db.CollectionTranslations
                .FirstOrDefaultAsync(t => t.CollectionId == c.Id && t.Locale == locale.Code, ct).ConfigureAwait(false);
            if (title.Length == 0)
            {
                if (existing is not null)
                {
                    db.CollectionTranslations.Remove(existing);
                }
            }
            else if (existing is null)
            {
                db.CollectionTranslations.Add(new CollectionTranslationRecord { CollectionId = c.Id, Locale = locale.Code, Title = title, Description = description });
            }
            else
            {
                existing.Title = title;
                existing.Description = description;
            }

            c.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync("curation.collection_changed", c, actor, ct, ("locale", locale.Code)).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Adds a published listing at the end, with the organiser's note on why it's there.</summary>
    public Task<Result> AddAsync(Guid id, Guid listingId, string? note, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(id, async (c, ct) =>
        {
            string? why = Optional(note);
            if (why is { Length: > MaxNote })
            {
                return Result.Failure(Error.Validation("collections.note", $"A note of up to {MaxNote} characters."));
            }

            if (!await db.Listings.AnyAsync(l => l.Id == listingId && l.Status == Published, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Validation("collections.listing", "Only a published listing can go in a collection."));
            }

            List<CollectionItemRecord> items = await ItemsAsync(c.Id, ct).ConfigureAwait(false);
            if (items.Any(i => i.ListingId == listingId))
            {
                return Result.Failure(Error.Conflict("collections.already_in", "That listing is in the collection already."));
            }

            if (items.Count >= MaxEntries)
            {
                return Result.Failure(Error.Conflict("collections.full", $"A collection holds up to {MaxEntries} listings."));
            }

            db.CollectionItems.Add(new CollectionItemRecord { CollectionId = c.Id, ListingId = listingId, SortOrder = (short)items.Count, CuratorNote = why });
            return await ChangedAsync(c, actor, ct, ("added", listingId.ToString())).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Changes the note on a listing in the collection; an empty one removes it.</summary>
    public Task<Result> NoteAsync(Guid id, Guid listingId, string? note, UserId actor, CancellationToken cancellationToken = default) =>
        WithItemAsync(id, listingId, async (c, items, item, ct) =>
        {
            string? why = Optional(note);
            if (why is { Length: > MaxNote })
            {
                return Result.Failure(Error.Validation("collections.note", $"A note of up to {MaxNote} characters."));
            }

            item.CuratorNote = why;
            return await ChangedAsync(c, actor, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Moves a listing up (<paramref name="by"/> −1) or down (+1) one place.</summary>
    public Task<Result> MoveAsync(Guid id, Guid listingId, int by, UserId actor, CancellationToken cancellationToken = default) =>
        WithItemAsync(id, listingId, async (c, items, item, ct) =>
        {
            int from = items.IndexOf(item);
            int to = from + Math.Sign(by);
            if (to >= 0 && to < items.Count)
            {
                items.RemoveAt(from);
                items.Insert(to, item);
                Renumber(items);
            }

            return await ChangedAsync(c, actor, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Takes a listing out of the collection.</summary>
    public Task<Result> RemoveAsync(Guid id, Guid listingId, UserId actor, CancellationToken cancellationToken = default) =>
        WithItemAsync(id, listingId, async (c, items, item, ct) =>
        {
            db.CollectionItems.Remove(item);
            items.Remove(item);
            Renumber(items);
            return await ChangedAsync(c, actor, ct, ("removed", listingId.ToString())).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Shows the collection on the site, within its dates. It needs a listing in it.</summary>
    public Task<Result> PublishAsync(Guid id, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(id, async (c, ct) =>
        {
            if (c.Status == Published)
            {
                return Result.Success();
            }

            if (!await db.CollectionItems.AnyAsync(i => i.CollectionId == c.Id, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("collections.empty", "Add a listing before publishing the collection."));
            }

            return await StateAsync(c, Published, "curation.collection_published", actor, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Takes the collection off the site. It can be published again.</summary>
    public Task<Result> ArchiveAsync(Guid id, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(id, (c, ct) => c.Status == Archived ? Task.FromResult(Result.Success()) : StateAsync(c, Archived, "curation.collection_archived", actor, ct), cancellationToken);

    /// <summary>Published listings whose title has the text in it, to add; none already in the collection. Up to 20.</summary>
    public Task<IReadOnlyList<ListingChoice>> FindListingsAsync(Guid id, string text, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ListingChoice>>(async ct =>
        {
            string words = (text ?? string.Empty).Trim();
            if (words.Length < 2)
            {
                return [];
            }

            string pattern = "%" + words.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
            return await db.Listings.AsNoTracking()
                .Where(l => l.Status == Published && EF.Functions.ILike(l.Title, pattern, @"\"))
                .Where(l => !db.CollectionItems.Any(i => i.CollectionId == id && i.ListingId == l.Id))
                .OrderBy(l => l.Title).ThenBy(l => l.Id)
                .Take(20)
                .Select(l => new ListingChoice(l.Id, l.Title, l.Slug))
                .ToListAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>The link name as stored: trimmed and in lower case.</summary>
    public static string Slug(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant();

    private static Error? Check(CollectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string title = input.Title.Trim();
        if (title.Length is 0 or > MaxTitle)
        {
            return Error.Validation("collections.title", $"A title of 1 to {MaxTitle} characters.");
        }

        if (Optional(input.Description) is { Length: > MaxDescription })
        {
            return Error.Validation("collections.description", $"A description of up to {MaxDescription} characters.");
        }

        if (!SlugPattern().IsMatch(Slug(input.Slug)))
        {
            return Error.Validation("collections.slug", "A link name of 3 to 60 lower-case letters, digits and hyphens.");
        }

        if (input.StartsOn is { } starts && input.EndsOn is { } ends && ends < starts)
        {
            return Error.Validation("collections.dates", "The last day can't be before the first.");
        }

        return input.SortOrder is < 0 or > MaxSortOrder
            ? Error.Validation("collections.order", $"A place from 0 to {MaxSortOrder}.")
            : null;
    }

    private static void Apply(CollectionRecord c, CollectionInput input, DateTimeOffset now)
    {
        c.Title = input.Title.Trim();
        c.Description = Optional(input.Description);
        c.StartsOn = input.StartsOn;
        c.EndsOn = input.EndsOn;
        c.IsFeatured = input.IsFeatured;
        c.SortOrder = input.SortOrder;
        c.UpdatedAt = now;
    }

    private async Task<Error?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("collections.slug_taken", "Another collection has that link name.");
        }
    }

    private Task<Result> WithAsync(Guid id, Func<CollectionRecord, CancellationToken, Task<Result>> work, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            CollectionRecord? c = await db.Collections.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            return c is null
                ? Result.Failure(Error.NotFound("collections.not_found", "There's no collection like that."))
                : await work(c, ct).ConfigureAwait(false);
        }, cancellationToken);

    private Task<Result> WithItemAsync(
        Guid id, Guid listingId, Func<CollectionRecord, List<CollectionItemRecord>, CollectionItemRecord, CancellationToken, Task<Result>> work, CancellationToken cancellationToken) =>
        WithAsync(id, async (c, ct) =>
        {
            List<CollectionItemRecord> items = await ItemsAsync(c.Id, ct).ConfigureAwait(false);
            return items.FirstOrDefault(i => i.ListingId == listingId) is { } item
                ? await work(c, items, item, ct).ConfigureAwait(false)
                : Result.Failure(Error.NotFound("collections.not_in", "That listing isn't in the collection."));
        }, cancellationToken);

    private Task<List<CollectionItemRecord>> ItemsAsync(Guid id, CancellationToken ct) =>
        db.CollectionItems.Where(i => i.CollectionId == id).OrderBy(i => i.SortOrder).ThenBy(i => i.ListingId).ToListAsync(ct);

    private static void Renumber(List<CollectionItemRecord> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i].SortOrder = (short)i;
        }
    }

    private async Task<Result> ChangedAsync(CollectionRecord c, UserId actor, CancellationToken ct, params (string Key, string Value)[] detail)
    {
        c.UpdatedAt = clock.StoredNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync("curation.collection_changed", c, actor, ct, detail).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> StateAsync(CollectionRecord c, string status, string action, UserId actor, CancellationToken ct)
    {
        c.Status = status;
        c.UpdatedAt = clock.StoredNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(action, c, actor, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Task AuditAsync(string action, CollectionRecord c, UserId actor, CancellationToken ct, params (string Key, string Value)[] detail)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal) { ["slug"] = c.Slug, ["status"] = c.Status };
        foreach ((string key, string value) in detail)
        {
            details[key] = value;
        }

        return audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            ActorId = actor,
            SubjectKind = "collection",
            SubjectIds = [c.Id],
            Detail = details,
        }, ct);
    }

    private async Task<CollectionDetail> ViewAsync(CollectionRecord c, CancellationToken ct)
    {
        List<CollectionWording> wordings = await db.CollectionTranslations.AsNoTracking().Where(t => t.CollectionId == c.Id)
            .OrderBy(t => t.Locale).Select(t => new CollectionWording(t.Locale, t.Title, t.Description)).ToListAsync(ct).ConfigureAwait(false);
        List<CuratedEntry> entries = await (from i in db.CollectionItems.AsNoTracking()
                                            join l in db.Listings.AsNoTracking() on i.ListingId equals l.Id
                                            where i.CollectionId == c.Id
                                            orderby i.SortOrder, i.ListingId
                                            select new CuratedEntry(l.Id, l.Title, l.Slug, l.Status == Published, i.CuratorNote)).ToListAsync(ct).ConfigureAwait(false);
        return new CollectionDetail(c.Id, c.Slug, c.Title, c.Description, c.Status, c.StartsOn, c.EndsOn, c.IsFeatured, c.SortOrder, c.Status != Draft, wordings, entries);
    }

    private static string? Optional(string? text) => text?.Trim() is { Length: > 0 } t ? t : null;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,58}[a-z0-9])$")]
    private static partial Regex SlugPattern();
}
