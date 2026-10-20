using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Personal;

public sealed record SavedEntry(Guid ListingId, string? Note, DateTimeOffset SavedAt);

public sealed record InterestEntry(string Code, string Name, int Weight);

/// <summary>Where someone searches from: nowhere in particular, or a neighbourhood. Never an address (D8).</summary>
public sealed record SearchAnchor(string? NeighbourhoodCode, int? MaxDistanceMetres);

/// <summary>
/// A signed-in person's own finder data, kept on the server so it follows them between devices:
/// saved listings, declared interests, where they search from, and the feed those make.
/// </summary>
/// <remarks>
/// Every method acts on the caller's own rows, named by its <c>user</c> argument from the
/// request's principal; nothing here takes another person's id from a client. Row-level security
/// adds the tenant and requires an actor.
/// </remarks>
public sealed class PersonalFinder(OpportunitiesDbContext db, ITaxonomyQueries taxonomy, IGeographyQueries geography, ITenantContext tenant, IClock clock)
{
    public const int MaxSaved = 200;
    public const int MaxInterests = 30;
    public const int MaxNoteLength = 500;
    public const int DefaultAnchorRadiusMetres = 5_000;
    private const string InterestVocabulary = "interest";

    // ---- saved listings ------------------------------------------------------------------

    public Task<IReadOnlyList<SavedEntry>> SavedAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<SavedEntry>>(async ct => await db.SavedItems.AsNoTracking()
            .Where(s => s.UserId == user.Value)
            .OrderByDescending(s => s.SavedAt)
            .Select(s => new SavedEntry(s.ListingId, s.Note, s.SavedAt))
            .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    /// <summary>Saves a published listing, or updates its note. Saving twice is not an error.</summary>
    public Task<Result<SavedEntry>> SaveAsync(UserId user, Guid listingId, string? note, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? kept = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (kept is { Length: > MaxNoteLength })
            {
                return Result<SavedEntry>.Failure(Error.Validation("saved.note_too_long", $"A note of at most {MaxNoteLength} characters."));
            }

            string published = ListingStatus.Published.ToDbValue();
            DateTimeOffset now = clock.UtcNow;
            bool live = await db.Listings.AsNoTracking().AnyAsync(
                l => l.Id == listingId && l.Status == published
                     && (l.PublishAt == null || l.PublishAt <= now) && (l.ExpiresAt == null || l.ExpiresAt > now), ct).ConfigureAwait(false);
            if (!live)
            {
                return Result<SavedEntry>.Failure(Error.NotFound("saved.listing_not_found", "No published listing has that id."));
            }

            SavedItemRecord? existing = await db.SavedItems.FirstOrDefaultAsync(s => s.UserId == user.Value && s.ListingId == listingId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                existing.Note = kept;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<SavedEntry>.Success(new SavedEntry(listingId, kept, existing.SavedAt));
            }

            if (await db.SavedItems.CountAsync(s => s.UserId == user.Value, ct).ConfigureAwait(false) >= MaxSaved)
            {
                return Result<SavedEntry>.Failure(Error.Conflict("saved.too_many", $"At most {MaxSaved} saved listings. Remove some first."));
            }

            SavedItemRecord saved = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                UserId = user.Value,
                ListingId = listingId,
                Note = kept,
                SavedAt = now,
            };
            db.SavedItems.Add(saved);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<SavedEntry>.Success(new SavedEntry(listingId, kept, now));
        }, cancellationToken);

    /// <summary>Forgets a saved listing. True when there was one.</summary>
    public Task<bool> RemoveAsync(UserId user, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await db.SavedItems.Where(s => s.UserId == user.Value && s.ListingId == listingId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0, cancellationToken);

    // ---- interests -----------------------------------------------------------------------

    public Task<IReadOnlyList<InterestEntry>> InterestsAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<InterestEntry>>(async ct =>
        {
            IReadOnlyList<VocabularyTerm> vocabulary = await taxonomy.GetVocabularyAsync(InterestVocabulary, tenant.Locale.Code, ct).ConfigureAwait(false);
            Dictionary<Guid, VocabularyTerm> byId = vocabulary.ToDictionary(t => t.Id);
            List<UserInterestRecord> mine = await db.UserInterests.AsNoTracking()
                .Where(i => i.UserId == user.Value)
                .ToListAsync(ct).ConfigureAwait(false);

            return [.. mine
                .Where(i => byId.ContainsKey(i.TermId))
                .OrderByDescending(i => i.Weight).ThenBy(i => byId[i.TermId].Name, StringComparer.CurrentCulture)
                .Select(i => new InterestEntry(byId[i.TermId].Code, byId[i.TermId].Name, i.Weight))];
        }, cancellationToken);

    /// <summary>Replaces the person's interests with these codes and weights (1 to 5).</summary>
    public Task<Result<IReadOnlyList<InterestEntry>>> SetInterestsAsync(UserId user, IReadOnlyDictionary<string, int> weights, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(weights);
            if (weights.Count > MaxInterests)
            {
                return Result<IReadOnlyList<InterestEntry>>.Failure(Error.Validation("interests.too_many", $"At most {MaxInterests} interests."));
            }

            if (weights.Values.Any(w => w is < 1 or > 5))
            {
                return Result<IReadOnlyList<InterestEntry>>.Failure(Error.Validation("interests.weight", "Weights run from 1 to 5."));
            }

            IReadOnlyList<VocabularyTerm> vocabulary = await taxonomy.GetVocabularyAsync(InterestVocabulary, tenant.Locale.Code, ct).ConfigureAwait(false);
            Dictionary<string, VocabularyTerm> byCode = vocabulary.ToDictionary(t => t.Code, StringComparer.Ordinal);
            string[] unknown = [.. weights.Keys.Where(k => !byCode.ContainsKey(k)).Order(StringComparer.Ordinal)];
            if (unknown.Length > 0)
            {
                return Result<IReadOnlyList<InterestEntry>>.Failure(Error.Validation("interests.unknown", "Not interests in this region: " + string.Join(", ", unknown),
                    arguments: new Dictionary<string, object> { ["codes"] = unknown }));
            }

            await db.UserInterests.Where(i => i.UserId == user.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            DateTimeOffset now = clock.UtcNow;
            foreach ((string code, int weight) in weights)
            {
                db.UserInterests.Add(new UserInterestRecord { UserId = user.Value, TermId = byCode[code].Id, Weight = (short)weight, DeclaredAt = now });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<IReadOnlyList<InterestEntry>>.Success([.. weights
                .OrderByDescending(w => w.Value).ThenBy(w => byCode[w.Key].Name, StringComparer.CurrentCulture)
                .Select(w => new InterestEntry(w.Key, byCode[w.Key].Name, w.Value))]);
        }, cancellationToken);

    // ---- where they search from ----------------------------------------------------------

    public Task<SearchAnchor> AnchorAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            UserSearchPreferenceRecord? pref = await db.UserSearchPreferences.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
            if (pref is not { AnchorKind: "neighbourhood", AnchorNeighbourhoodId: { } hoodId })
            {
                return new SearchAnchor(null, pref?.MaxDistanceM);
            }

            IReadOnlyList<NeighbourhoodShape> hoods = await geography.GetNeighbourhoodsAsync(tenant.Locale.Code, ct).ConfigureAwait(false);
            return new SearchAnchor(hoods.FirstOrDefault(h => h.Id == hoodId)?.Code, pref.MaxDistanceM);
        }, cancellationToken);

    /// <summary>Sets where the person searches from: a neighbourhood by its code, or none.</summary>
    public Task<Result<SearchAnchor>> SetAnchorAsync(UserId user, SearchAnchor anchor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(anchor);
            if (anchor.MaxDistanceMetres is { } m && (m < 100 || m > 80_000))
            {
                return Result<SearchAnchor>.Failure(Error.Validation("anchor.distance", "A distance from 100 to 80,000 metres."));
            }

            Guid? hoodId = null;
            if (anchor.NeighbourhoodCode is { Length: > 0 } code)
            {
                IReadOnlyList<NeighbourhoodShape> hoods = await geography.GetNeighbourhoodsAsync(tenant.Locale.Code, ct).ConfigureAwait(false);
                hoodId = hoods.FirstOrDefault(h => string.Equals(h.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
                if (hoodId is null)
                {
                    return Result<SearchAnchor>.Failure(Error.Validation("anchor.neighbourhood", "No neighbourhood has that code."));
                }
            }

            UserSearchPreferenceRecord? pref = await db.UserSearchPreferences.FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
            if (pref is null)
            {
                pref = new UserSearchPreferenceRecord { UserId = user.Value, TenantId = tenant.TenantId.Value };
                db.UserSearchPreferences.Add(pref);
            }

            pref.AnchorKind = hoodId is null ? "none" : "neighbourhood";
            pref.AnchorNeighbourhoodId = hoodId;
            pref.AnchorSchoolId = null;
            pref.MaxDistanceM = anchor.MaxDistanceMetres;
            pref.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<SearchAnchor>.Success(anchor with { NeighbourhoodCode = hoodId is null ? null : anchor.NeighbourhoodCode });
        }, cancellationToken);

    // ---- the feed ------------------------------------------------------------------------

    /// <summary>
    /// The person's feed as a finder query: listings matching their interests, ranked with those
    /// interests (which is where each item's "why" comes from), near their anchor when they have
    /// one. With no interests it is what starts soonest nearby.
    /// </summary>
    public Task<SearchQuery> FeedQueryAsync(UserId user, Cursor? after, PageSize pageSize, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            IReadOnlyList<InterestEntry> interests = await InterestsAsync(user, ct).ConfigureAwait(false);
            string[] tokens = [.. interests.Select(i => "int:" + i.Code)];

            Coordinate? origin = null;
            Distance? radius = null;
            UserSearchPreferenceRecord? pref = await db.UserSearchPreferences.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
            if (pref is { AnchorKind: "neighbourhood", AnchorNeighbourhoodId: { } hoodId })
            {
                IReadOnlyList<NeighbourhoodShape> hoods = await geography.GetNeighbourhoodsAsync(tenant.Locale.Code, ct).ConfigureAwait(false);
                if (hoods.FirstOrDefault(h => h.Id == hoodId) is { } hood)
                {
                    // The neighbourhood's centre: public geography, the same point for everyone in it.
                    origin = new Coordinate(hood.CentroidLatitude, hood.CentroidLongitude);
                    radius = new Distance(pref.MaxDistanceM ?? DefaultAnchorRadiusMetres);
                }
            }

            return new SearchQuery
            {
                TenantId = tenant.TenantId.Value,
                TextSearchConfig = tenant.Locale.TextSearchConfig,
                AnyFacets = tokens.Length == 0 ? null : tokens,
                InterestTokens = tokens.Length == 0 ? null : tokens,
                Origin = origin,
                Radius = radius,
                Sort = tokens.Length == 0 ? SearchSort.Soonest : SearchSort.Relevance,
                After = after,
                PageSize = pageSize,
            };
        }, cancellationToken);
}
