using System.Text.Json;
using System.Text.Json.Nodes;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Curation;

/// <summary>A question and its answer on a neighbourhood page.</summary>
public sealed record PageQuestion(string Question, string Answer);

/// <summary>Someone to ask, on a neighbourhood page. Everything but the name is optional.</summary>
public sealed record PageContact(string Name, string? Role, string? Phone, string? Email, string? Url);

/// <summary>
/// One section of a neighbourhood page as the organiser writes it. What's read depends on the
/// kind: text and getting around take a heading and a body; questions take <c>Questions</c>;
/// people to ask take <c>Contacts</c>; a list of activities takes <c>ListingKind</c> (null for any)
/// and <c>Limit</c>; a collection takes <c>Collection</c>, a published collection's link name.
/// </summary>
public sealed record PageBlockInput(
    string Kind,
    string? Heading,
    string? Body = null,
    IReadOnlyList<PageQuestion>? Questions = null,
    IReadOnlyList<PageContact>? Contacts = null,
    string? ListingKind = null,
    int Limit = 6,
    string? Collection = null);

/// <summary>
/// A section as stored, read back for editing. <c>Editable</c> is false for a kind this editor
/// doesn't write (it can only be moved or removed); <c>Shown</c> is false when the site leaves it
/// out, as it does a collection that isn't on the site.
/// </summary>
public sealed record EditedBlock(Guid Id, PageBlockInput Content, bool Editable, bool Shown);

/// <summary>A neighbourhood in the organiser's list, and its page if it has one.</summary>
public sealed record HoodPageRow(Guid NeighbourhoodId, string Code, string Name, string? Status, IReadOnlyList<string> Languages);

/// <summary>A neighbourhood's page as the organiser edits it, its sections in one language.</summary>
public sealed record HoodPageDetail(
    Guid Id, string Status, IReadOnlyDictionary<string, int> Languages, IReadOnlyList<EditedBlock> Blocks, PageHero? Hero = null);

/// <summary>A page's hero image (D103) and what it shows, by locale; English is always there.</summary>
public sealed record PageHero(Guid MediaId, IReadOnlyDictionary<string, string> Alts);

/// <summary>A published collection a page can point to.</summary>
public sealed record CollectionChoice(string Slug, string Title);

/// <summary>
/// The organiser's neighbourhood pages (D95): the sections a neighbourhood's public page shows
/// above what's on there, written in English and, whole, in the site's other languages; ordered,
/// published and archived.
/// </summary>
/// <remarks>
/// <para>
/// The public side is <see cref="CurationQueries.GetNeighbourhoodPageAsync"/>: it shows a
/// published page in the reader's language if it has any section in it, otherwise in English, and
/// never mixes the two. So a language here is a whole version of the page, not a translation of
/// one section.
/// </para>
/// <para>
/// The sections hold the organiser's text and pointers: a list of activities is the finder's
/// (published only), and a collection is shown only while it's on the site. Nothing in a page is
/// about a person, other than the contacts the organiser chooses to list. A page never goes back to
/// draft; an archived one is published again. Every change is audited.
/// </para>
/// </remarks>
public sealed class NeighbourhoodPageEditor(
    OpportunitiesDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit, IGeographyQueries geography, IMediaLibrary media)
{
    public const int MaxBlocks = 20;
    public const int MaxHeading = 120;
    public const int MaxBody = 4000;
    public const int MaxQuestions = 20;
    public const int MaxQuestion = 200;
    public const int MaxAnswer = 1000;
    public const int MaxContacts = 12;
    public const int MaxContactText = 120;
    public const int MaxPhone = 40;
    public const int MaxEmail = 254;
    public const int MaxUrl = 300;
    public const int MaxLimit = 24;

    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    /// <summary>Untranslated page text is the organiser's, in English (as <see cref="CurationQueries"/> reads it).</summary>
    public const string SourceLanguage = "en";

    public const string RichText = "rich_text";
    public const string Transit = "transit";
    public const string Faq = "faq";
    public const string ContactList = "contact_list";
    public const string ListingList = "listing_list";
    public const string CollectionBlock = "collection";

    /// <summary>The kinds this editor writes, in the order it offers them. The site draws each.</summary>
    public static readonly IReadOnlyList<string> Kinds = [RichText, ListingList, CollectionBlock, ContactList, Faq, Transit];

    /// <summary>The kinds a list of activities can be narrowed to; none means any.</summary>
    public static readonly IReadOnlyList<string> ListingKinds = ["program", "event", "job", "resource"];

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Every neighbourhood, by name, with its page's state and languages if it has one.</summary>
    public Task<IReadOnlyList<HoodPageRow>> ListAsync(string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<HoodPageRow>>(async ct =>
        {
            IReadOnlyList<NeighbourhoodShape> hoods = await geography.GetNeighbourhoodsAsync(locale, ct).ConfigureAwait(false);
            var pages = await db.NeighbourhoodPages.AsNoTracking()
                .Select(p => new { p.NeighbourhoodId, p.Status, Languages = db.NeighbourhoodPageBlocks.Where(b => b.PageId == p.Id).Select(b => b.Locale).Distinct().ToList() })
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. hoods.OrderBy(h => h.Name, StringComparer.CurrentCulture).Select(h =>
            {
                var page = pages.FirstOrDefault(p => p.NeighbourhoodId == h.Id);
                return new HoodPageRow(h.Id, h.Code, h.Name, page?.Status, page is null ? [] : [.. page.Languages.Order(StringComparer.Ordinal)]);
            })];
        }, cancellationToken);

    /// <summary>The neighbourhood's page with its sections in <paramref name="locale"/>, or null if it has none yet.</summary>
    public Task<HoodPageDetail?> GetAsync(Guid neighbourhoodId, string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            NeighbourhoodPageRecord? page = await db.NeighbourhoodPages.AsNoTracking().FirstOrDefaultAsync(p => p.NeighbourhoodId == neighbourhoodId, ct).ConfigureAwait(false);
            if (page is null)
            {
                return null;
            }

            Dictionary<string, int> languages = await db.NeighbourhoodPageBlocks.AsNoTracking().Where(b => b.PageId == page.Id)
                .GroupBy(b => b.Locale).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, StringComparer.Ordinal, ct).ConfigureAwait(false);
            List<NeighbourhoodPageBlockRecord> blocks = await BlocksAsync(page.Id, locale, ct, tracked: false).ConfigureAwait(false);
            HashSet<string> shown = [.. await ShownCollectionsAsync(ct).ConfigureAwait(false)];
            return new HoodPageDetail(page.Id, page.Status, languages, [.. blocks.Select(b => Read(b, shown))],
                page.HeroMediaId is { } hero ? new PageHero(hero, CurationImages.Alts(page.HeroAlt)) : null);
        }, cancellationToken);

    /// <summary>Begins the neighbourhood's page as a draft. Beginning one that exists does nothing.</summary>
    public Task<Result> CreateAsync(Guid neighbourhoodId, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.NeighbourhoodPages.AnyAsync(p => p.NeighbourhoodId == neighbourhoodId, ct).ConfigureAwait(false))
            {
                return Result.Success();
            }

            // The neighbourhood must be this tenant's: a foreign key check doesn't see row security.
            IReadOnlyList<NeighbourhoodShape> hoods = await geography.GetNeighbourhoodsAsync(SourceLanguage, ct).ConfigureAwait(false);
            if (hoods.All(h => h.Id != neighbourhoodId))
            {
                return Result.Failure(NotFound());
            }

            NeighbourhoodPageRecord page = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                NeighbourhoodId = neighbourhoodId,
                Status = Draft,
                OrganiserUserId = actor.Value,
                UpdatedAt = clock.StoredNow(),
            };
            db.NeighbourhoodPages.Add(page);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync("curation.neighbourhood_page_created", page, actor, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Adds a section at the end of the page in <paramref name="locale"/>.</summary>
    public Task<Result> AddAsync(Guid neighbourhoodId, string locale, PageBlockInput input, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(neighbourhoodId, async (page, ct) =>
        {
            if (!Locale.TryFromCode(locale, out Locale language))
            {
                return Result.Failure(Error.Validation("hood_pages.locale", "One of the site's languages."));
            }

            List<NeighbourhoodPageBlockRecord> blocks = await BlocksAsync(page.Id, language.Code, ct).ConfigureAwait(false);
            if (blocks.Count >= MaxBlocks)
            {
                return Result.Failure(Error.Conflict("hood_pages.full", $"A page holds up to {MaxBlocks} sections in each language."));
            }

            if (await CheckAsync(input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result.Failure(invalid);
            }

            NeighbourhoodPageBlockRecord block = new() { Id = UuidV7.NewId(), PageId = page.Id, Locale = language.Code, BlockKind = input.Kind, SortOrder = (short)blocks.Count };
            Write(block, input);
            db.NeighbourhoodPageBlocks.Add(block);
            return await ChangedAsync(page, actor, ct, ("locale", language.Code), ("added", input.Kind)).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Rewrites a section. Its kind stays.</summary>
    public Task<Result> UpdateAsync(Guid neighbourhoodId, Guid blockId, PageBlockInput input, UserId actor, CancellationToken cancellationToken = default) =>
        WithBlockAsync(neighbourhoodId, blockId, async (page, blocks, block, ct) =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!Kinds.Contains(block.BlockKind))
            {
                return Result.Failure(Error.Validation("hood_pages.kind", "That kind of section can only be moved or removed."));
            }

            PageBlockInput content = input with { Kind = block.BlockKind };
            if (await CheckAsync(content, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result.Failure(invalid);
            }

            Write(block, content);
            return await ChangedAsync(page, actor, ct, ("locale", block.Locale)).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Moves a section up (<paramref name="by"/> −1) or down (+1) one place among its language's.</summary>
    public Task<Result> MoveAsync(Guid neighbourhoodId, Guid blockId, int by, UserId actor, CancellationToken cancellationToken = default) =>
        WithBlockAsync(neighbourhoodId, blockId, async (page, blocks, block, ct) =>
        {
            int from = blocks.IndexOf(block);
            int to = from + Math.Sign(by);
            if (to >= 0 && to < blocks.Count)
            {
                blocks.RemoveAt(from);
                blocks.Insert(to, block);
                Renumber(blocks);
            }

            return await ChangedAsync(page, actor, ct, ("locale", block.Locale)).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Takes a section off the page.</summary>
    public Task<Result> RemoveAsync(Guid neighbourhoodId, Guid blockId, UserId actor, CancellationToken cancellationToken = default) =>
        WithBlockAsync(neighbourhoodId, blockId, async (page, blocks, block, ct) =>
        {
            db.NeighbourhoodPageBlocks.Remove(block);
            blocks.Remove(block);
            Renumber(blocks);
            return await ChangedAsync(page, actor, ct, ("locale", block.Locale), ("removed", block.BlockKind)).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Puts a picture across the top of the page (D103), with what it shows in English; it
    /// replaces any before it, whose descriptions in other languages go with it.
    /// </summary>
    public async Task<Result> SetHeroAsync(Guid neighbourhoodId, ReadOnlyMemory<byte> file, string? alt, UserId actor, CancellationToken cancellationToken = default)
    {
        Result<string> said = CurationImages.Alt(alt);
        if (!said.IsSuccess)
        {
            return Result.Failure(said.Error!);
        }

        if (!await db.InTransactionAsync(ct => db.NeighbourhoodPages.AnyAsync(p => p.NeighbourhoodId == neighbourhoodId, ct), cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(NotFound());
        }

        if (await media.AddImageAsync(file, CurationImages.Hero, MediaVisibility.Public, actor, cancellationToken).ConfigureAwait(false) is not { } image)
        {
            return Result.Failure(CurationImages.NotAnImage);
        }

        Guid? before = null;
        Result set = await WithAsync(neighbourhoodId, (page, ct) =>
        {
            before = page.HeroMediaId;
            page.HeroMediaId = image.MediaId;
            page.HeroAlt = CurationImages.Json(new Dictionary<string, string>(StringComparer.Ordinal) { [SourceLanguage] = said.Value });
            return ChangedAsync(page, actor, ct, ("hero", image.MediaId.ToString()));
        }, cancellationToken).ConfigureAwait(false);

        // The bytes are kept in Platform's own transaction; whichever picture isn't used now goes.
        if ((set.IsSuccess ? before : image.MediaId) is { } unused)
        {
            await media.RemoveAsync(unused, actor, cancellationToken).ConfigureAwait(false);
        }

        return set;
    }

    /// <summary>
    /// What the hero image shows in <paramref name="locale"/>. English can be changed but not
    /// emptied; an empty description in another language takes it away, and English is read instead.
    /// </summary>
    public Task<Result> DescribeHeroAsync(Guid neighbourhoodId, string locale, string? alt, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(neighbourhoodId, (page, ct) =>
        {
            if (!Locale.TryFromCode(locale, out Locale language))
            {
                return Task.FromResult(Result.Failure(Error.Validation("hood_pages.locale", "One of the site's languages.")));
            }

            if (page.HeroMediaId is null)
            {
                return Task.FromResult(Result.Failure(Error.Conflict("curation.no_hero", "There's no picture to describe.")));
            }

            Dictionary<string, string> alts = CurationImages.Alts(page.HeroAlt);
            if (string.IsNullOrWhiteSpace(alt) && language.Code != SourceLanguage)
            {
                alts.Remove(language.Code);
            }
            else
            {
                Result<string> said = CurationImages.Alt(alt);
                if (!said.IsSuccess)
                {
                    return Task.FromResult(Result.Failure(said.Error!));
                }

                alts[language.Code] = said.Value;
            }

            page.HeroAlt = CurationImages.Json(alts);
            return ChangedAsync(page, actor, ct, ("hero_alt", language.Code));
        }, cancellationToken);

    /// <summary>Takes the picture away, and its bytes with it.</summary>
    public async Task<Result> RemoveHeroAsync(Guid neighbourhoodId, UserId actor, CancellationToken cancellationToken = default)
    {
        Guid? before = null;
        Result removed = await WithAsync(neighbourhoodId, (page, ct) =>
        {
            before = page.HeroMediaId;
            if (before is null)
            {
                return Task.FromResult(Result.Success());
            }

            page.HeroMediaId = null;
            page.HeroAlt = "{}";
            return ChangedAsync(page, actor, ct, ("hero", "removed"));
        }, cancellationToken).ConfigureAwait(false);

        if (removed.IsSuccess && before is { } gone)
        {
            await media.RemoveAsync(gone, actor, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>Shows the page on the site. It needs a section in English, which every reader can fall back to.</summary>
    public Task<Result> PublishAsync(Guid neighbourhoodId, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(neighbourhoodId, async (page, ct) =>
        {
            if (page.Status == Published)
            {
                return Result.Success();
            }

            if (!await db.NeighbourhoodPageBlocks.AnyAsync(b => b.PageId == page.Id && b.Locale == SourceLanguage, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("hood_pages.empty", "Write a section in English before publishing the page."));
            }

            return await StateAsync(page, Published, "curation.neighbourhood_page_published", actor, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Takes the page off the site; the neighbourhood's page shows only what's on there. It can be published again.</summary>
    public Task<Result> ArchiveAsync(Guid neighbourhoodId, UserId actor, CancellationToken cancellationToken = default) =>
        WithAsync(neighbourhoodId, (page, ct) => page.Status == Archived
            ? Task.FromResult(Result.Success())
            : StateAsync(page, Archived, "curation.neighbourhood_page_archived", actor, ct), cancellationToken);

    /// <summary>The published collections a page can point to, in the site's order.</summary>
    public Task<IReadOnlyList<CollectionChoice>> CollectionsAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<CollectionChoice>>(async ct =>
            await db.Collections.AsNoTracking().Where(c => c.Status == Published)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Title)
                .Select(c => new CollectionChoice(c.Slug, c.Title))
                .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    private async Task<Error?> CheckAsync(PageBlockInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Kinds.Contains(input.Kind))
        {
            return Error.Validation("hood_pages.kind", "A kind of section the site shows.");
        }

        if (Optional(input.Heading) is { Length: > MaxHeading })
        {
            return Error.Validation("hood_pages.heading", $"A heading of up to {MaxHeading} characters.");
        }

        switch (input.Kind)
        {
            case RichText or Transit:
                return Optional(input.Body) is { Length: > 0 and <= MaxBody }
                    ? null
                    : Error.Validation("hood_pages.body", $"Some text, up to {MaxBody} characters.");

            case Faq:
                List<PageQuestion> questions = Questions(input.Questions);
                if (questions.Count is 0 or > MaxQuestions)
                {
                    return Error.Validation("hood_pages.questions", $"From 1 to {MaxQuestions} questions.");
                }

                return questions.Any(q => q.Question.Length is 0 or > MaxQuestion || q.Answer.Length is 0 or > MaxAnswer)
                    ? Error.Validation("hood_pages.question", $"Each question needs an answer: a question of up to {MaxQuestion} characters, an answer of up to {MaxAnswer}.")
                    : null;

            case ContactList:
                List<PageContact> contacts = Contacts(input.Contacts);
                if (contacts.Count is 0 or > MaxContacts)
                {
                    return Error.Validation("hood_pages.contacts", $"From 1 to {MaxContacts} people to ask.");
                }

                foreach (PageContact c in contacts)
                {
                    if (c.Name.Length is 0 or > MaxContactText || c.Role is { Length: > MaxContactText } || c.Phone is { Length: > MaxPhone })
                    {
                        return Error.Validation("hood_pages.contact", $"Each person to ask needs a name; a name and a role of up to {MaxContactText} characters, a phone number of up to {MaxPhone}.");
                    }

                    if (c.Email is { } email && !IsEmail(email))
                    {
                        return Error.Validation("hood_pages.email", "An email address like name@example.org.");
                    }

                    if (c.Url is { } url && !IsWebAddress(url))
                    {
                        return Error.Validation("hood_pages.url", "A web address beginning https:// or http://.");
                    }
                }

                return null;

            case ListingList:
                if (input.ListingKind is { } kind && !ListingKinds.Contains(kind))
                {
                    return Error.Validation("hood_pages.listing_kind", "Programs, events, jobs, resources or any.");
                }

                return input.Limit is < 1 or > MaxLimit
                    ? Error.Validation("hood_pages.limit", $"From 1 to {MaxLimit} activities.")
                    : null;

            default:
                string slug = CollectionEditor.Slug(input.Collection);
                return await db.Collections.AnyAsync(c => c.Slug == slug && c.Status == Published, ct).ConfigureAwait(false)
                    ? null
                    : Error.Validation("hood_pages.collection", "A collection that's on the site.");
        }
    }

    /// <summary>Stores the section the way the public page reads it.</summary>
    private static void Write(NeighbourhoodPageBlockRecord block, PageBlockInput input)
    {
        block.Heading = Optional(input.Heading);
        block.BodyMd = input.Kind is RichText or Transit ? Optional(input.Body) : null;
        JsonObject payload = [];
        switch (input.Kind)
        {
            case Faq:
                payload["items"] = new JsonArray([.. Questions(input.Questions).Select(q => (JsonNode)new JsonObject { ["q"] = q.Question, ["a"] = q.Answer })]);
                break;
            case ContactList:
                payload["contacts"] = new JsonArray([.. Contacts(input.Contacts).Select(c =>
                {
                    JsonObject o = new() { ["name"] = c.Name };
                    foreach ((string key, string? value) in new[] { ("role", c.Role), ("phone", c.Phone), ("email", c.Email), ("url", c.Url) })
                    {
                        if (value is not null)
                        {
                            o[key] = value;
                        }
                    }

                    return (JsonNode)o;
                })]);
                break;
            case ListingList:
                if (input.ListingKind is { } kind)
                {
                    payload["kind"] = kind;
                }

                payload["limit"] = input.Limit;
                break;
            case CollectionBlock:
                payload["collection"] = CollectionEditor.Slug(input.Collection);
                break;
        }

        block.Payload = payload.ToJsonString(Compact);
    }

    /// <summary>A stored section back as the organiser wrote it. A payload that doesn't parse reads as empty.</summary>
    private static EditedBlock Read(NeighbourhoodPageBlockRecord b, HashSet<string> shownCollections)
    {
        JsonObject? payload = null;
        try
        {
            payload = JsonNode.Parse(b.Payload) as JsonObject;
        }
        catch (JsonException)
        {
        }

        string? Text(JsonNode? node, string name) => node is JsonObject o && o[name] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;
        IEnumerable<JsonNode> Items(string name) => payload?[name] is JsonArray a ? a.OfType<JsonNode>() : [];

        int limit = payload?["limit"] is JsonValue lv && lv.TryGetValue(out int n) ? n : 6;
        string? collection = Text(payload, "collection");
        PageBlockInput content = new(
            b.BlockKind,
            b.Heading,
            b.BodyMd,
            [.. Items("items").Select(i => new PageQuestion(Text(i, "q") ?? "", Text(i, "a") ?? ""))],
            [.. Items("contacts").Select(c => new PageContact(Text(c, "name") ?? "", Text(c, "role"), Text(c, "phone"), Text(c, "email"), Text(c, "url")))],
            Text(payload, "kind"),
            limit,
            collection);
        bool editable = Kinds.Contains(b.BlockKind);
        bool shown = editable && (b.BlockKind != CollectionBlock || (collection is not null && shownCollections.Contains(collection)));
        return new EditedBlock(b.Id, content, editable, shown);
    }

    /// <summary>The questions with something in them, trimmed; rows left wholly empty are dropped.</summary>
    private static List<PageQuestion> Questions(IReadOnlyList<PageQuestion>? questions) =>
        [.. (questions ?? []).Select(q => new PageQuestion(q.Question?.Trim() ?? "", q.Answer?.Trim() ?? "")).Where(q => q.Question.Length > 0 || q.Answer.Length > 0)];

    /// <summary>The people with something in their row, trimmed; rows left wholly empty are dropped.</summary>
    private static List<PageContact> Contacts(IReadOnlyList<PageContact>? contacts) =>
        [.. (contacts ?? [])
            .Select(c => new PageContact(c.Name?.Trim() ?? "", Optional(c.Role), Optional(c.Phone), Optional(c.Email), Optional(c.Url)))
            .Where(c => c.Name.Length > 0 || c.Role is not null || c.Phone is not null || c.Email is not null || c.Url is not null)];

    private static bool IsEmail(string email)
    {
        int at = email.IndexOf('@', StringComparison.Ordinal);
        return email.Length <= MaxEmail && at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1
            && !email.EndsWith('.') && !email.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\'' or '?');
    }

    private static bool IsWebAddress(string url) =>
        url.Length <= MaxUrl
        && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Length > 0
        && !url.Any(char.IsWhiteSpace);

    private async Task<string[]> ShownCollectionsAsync(CancellationToken ct)
    {
        DateOnly today = clock.TodayIn(tenant.TimeZone);
        return await db.Collections.AsNoTracking()
            .Where(c => c.Status == Published && (c.StartsOn == null || c.StartsOn <= today) && (c.EndsOn == null || c.EndsOn >= today))
            .Select(c => (string)c.Slug).ToArrayAsync(ct).ConfigureAwait(false);
    }

    private Task<Result> WithAsync(Guid neighbourhoodId, Func<NeighbourhoodPageRecord, CancellationToken, Task<Result>> work, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            NeighbourhoodPageRecord? page = await db.NeighbourhoodPages.FirstOrDefaultAsync(p => p.NeighbourhoodId == neighbourhoodId, ct).ConfigureAwait(false);
            return page is null ? Result.Failure(NotFound()) : await work(page, ct).ConfigureAwait(false);
        }, cancellationToken);

    private Task<Result> WithBlockAsync(
        Guid neighbourhoodId, Guid blockId,
        Func<NeighbourhoodPageRecord, List<NeighbourhoodPageBlockRecord>, NeighbourhoodPageBlockRecord, CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken) =>
        WithAsync(neighbourhoodId, async (page, ct) =>
        {
            string? locale = await db.NeighbourhoodPageBlocks.Where(b => b.Id == blockId && b.PageId == page.Id).Select(b => b.Locale).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (locale is null)
            {
                return Result.Failure(Error.NotFound("hood_pages.no_block", "That section isn't on the page."));
            }

            List<NeighbourhoodPageBlockRecord> blocks = await BlocksAsync(page.Id, locale, ct).ConfigureAwait(false);
            return await work(page, blocks, blocks.Single(b => b.Id == blockId), ct).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<List<NeighbourhoodPageBlockRecord>> BlocksAsync(Guid pageId, string locale, CancellationToken ct, bool tracked = true)
    {
        IQueryable<NeighbourhoodPageBlockRecord> blocks = tracked ? db.NeighbourhoodPageBlocks : db.NeighbourhoodPageBlocks.AsNoTracking();
        return await blocks.Where(b => b.PageId == pageId && b.Locale == locale)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static void Renumber(List<NeighbourhoodPageBlockRecord> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            blocks[i].SortOrder = (short)i;
        }
    }

    private async Task<Result> ChangedAsync(NeighbourhoodPageRecord page, UserId actor, CancellationToken ct, params (string Key, string Value)[] detail)
    {
        page.UpdatedAt = clock.StoredNow();
        page.OrganiserUserId = actor.Value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync("curation.neighbourhood_page_changed", page, actor, ct, detail).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> StateAsync(NeighbourhoodPageRecord page, string status, string action, UserId actor, CancellationToken ct)
    {
        page.Status = status;
        page.UpdatedAt = clock.StoredNow();
        page.OrganiserUserId = actor.Value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(action, page, actor, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Task AuditAsync(string action, NeighbourhoodPageRecord page, UserId actor, CancellationToken ct, params (string Key, string Value)[] detail)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal) { ["neighbourhood_id"] = page.NeighbourhoodId.ToString(), ["status"] = page.Status };
        foreach ((string key, string value) in detail)
        {
            details[key] = value;
        }

        return audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            ActorId = actor,
            SubjectKind = "neighbourhood_page",
            SubjectIds = [page.Id],
            Detail = details,
        }, ct);
    }

    private static Error NotFound() => Error.NotFound("hood_pages.not_found", "There's no neighbourhood page like that.");

    private static string? Optional(string? text) => text?.Trim() is { Length: > 0 } t ? t : null;
}
