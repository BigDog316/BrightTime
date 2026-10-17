using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Domain;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>A listing opened in the editor: its form, and the state around it.</summary>
public sealed record EditableListing(
    Guid Id,
    OrganisationId OrganisationId,
    ListingStatus Status,
    string Slug,
    string? ReviewNote,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReviewedAt,
    ListingForm Form)
{
    /// <summary>Published and edited since an organiser last looked (D15).</summary>
    public bool ChangedSinceReview => Status == ListingStatus.Published && (ReviewedAt is null || UpdatedAt > ReviewedAt);
}

/// <summary>What a save did: the listing's id, or why nothing was written.</summary>
public sealed record SaveOutcome(Guid? ListingId, IReadOnlyDictionary<string, string[]> Errors, string? Problem)
{
    public bool Saved => ListingId is not null && Errors.Count == 0 && Problem is null;

    internal static SaveOutcome Invalid(IReadOnlyDictionary<string, string[]> errors) => new(null, errors, null);

    internal static SaveOutcome Refused(string problem) => new(null, new Dictionary<string, string[]>(), problem);
}

/// <summary>One row of a provider's listing list, or of the review queue.</summary>
public sealed record ListingRow(
    Guid Id,
    OrganisationId OrganisationId,
    string Slug,
    string Title,
    ListingKind Kind,
    ListingStatus Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

/// <summary>The listings waiting for an organiser.</summary>
public sealed record ListingsForReview(IReadOnlyList<ListingRow> AwaitingReview, IReadOnlyList<ListingRow> ChangedSinceReview);

/// <summary>
/// Creates and edits listings for their organisation's staff, and moves them through
/// <see cref="ListingLifecycle"/> for staff and organisers.
/// </summary>
/// <remarks>
/// <para>
/// Authorisation is the caller's: this class is told which organisation is acting and which
/// mover the caller is, and checks only that the listing belongs to that organisation. Every
/// write schedules the listing's projection in the same transaction, so search catches up
/// when the worker runs, and never sees a change that rolled back.
/// </para>
/// <para>
/// An edit to a published listing goes live and puts the listing in the organiser's
/// "changed since review" lane (decision D15).
/// </para>
/// </remarks>
public sealed class ListingEditor(
    OpportunitiesDbContext db,
    IDirectoryQueries directory,
    ITaxonomyQueries taxonomy,
    ITenantContext tenant,
    IClock clock)
{
    public const string Zone = "America/New_York";

    private static readonly ListingStatus[] Editable =
        [ListingStatus.Draft, ListingStatus.ChangesRequested, ListingStatus.Published, ListingStatus.Unpublished];

    public static bool IsEditable(ListingStatus status) => Editable.Contains(status);

    public async Task<EditableListing?> LoadAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(async ct =>
        {
            var row = await db.Listings.AsNoTracking()
                .Where(l => l.Id == listingId)
                .Select(l => new { Listing = l, Version = EF.Property<uint>(l, "xmin") })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            ListingRecord l = row.Listing;
            List<Guid> sites = await db.ListingSites.AsNoTracking().Where(x => x.ListingId == listingId).Select(x => x.SiteId).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> terms = await db.ListingTerms.AsNoTracking().Where(x => x.ListingId == listingId).Select(x => x.TermId).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> accommodations = await db.ListingAccommodations.AsNoTracking().Where(x => x.ListingId == listingId).Select(x => x.TermId).ToListAsync(ct).ConfigureAwait(false);
            List<string> languages = await db.ListingLanguages.AsNoTracking().Where(x => x.ListingId == listingId).Select(x => x.LanguageCode).ToListAsync(ct).ConfigureAwait(false);
            ListingSessionRecord? session = await db.Sessions.AsNoTracking().Where(s => s.ListingId == listingId && !s.IsCancelled).OrderBy(s => s.StartsOn).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            ListingDeadlineRecord? deadline = await db.ListingDeadlines.AsNoTracking().Where(d => d.ListingId == listingId).OrderBy(d => d.DueAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);

            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
            var form = new ListingForm
            {
                Kind = l.Kind,
                Title = l.Title,
                Summary = l.Summary,
                Description = l.Description ?? string.Empty,
                PrimaryLanguage = l.PrimaryLanguage,
                AgeMin = Text(l.AgeMin),
                AgeMax = Text(l.AgeMax),
                GradeMin = Text(l.GradeMin),
                GradeMax = Text(l.GradeMax),
                TermIds = terms,
                AccommodationIds = accommodations,
                Languages = languages,
                Modality = l.Modality,
                VirtualUrl = l.VirtualUrl ?? string.Empty,
                SiteIds = sites,
                CostModel = l.CostModel,
                CostMin = Dollars(l.CostCentsMin),
                CostMax = Dollars(l.CostCentsMax),
                SlidingScaleNote = l.SlidingScaleNote ?? string.Empty,
                RegistrationMode = l.RegistrationMode,
                ExternalUrl = l.ExternalUrl ?? string.Empty,
                Capacity = Text(l.CapacityTotal),
                MealsProvided = l.MealsProvided,
                TransportProvided = l.TransportProvided,
                TransportNote = l.TransportNote ?? string.Empty,
                IsDropIn = l.IsDropIn,
                AccessibilityNote = l.AccessibilityNote ?? string.Empty,
                ContactEmail = l.ContactEmail ?? string.Empty,
                ContactPhone = l.ContactPhone ?? string.Empty,
                Repeats = session?.Rrule is null && session is not null ? "once" : "weekly",
                Days = session?.Rrule is { } rrule ? DaysOf(rrule) : [],
                StartsOn = session?.StartsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                EndsOn = session?.Rrule is not null ? session.EndsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty : string.Empty,
                StartTime = session?.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                EndTime = session?.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                DeadlineKind = deadline?.DeadlineKind ?? string.Empty,
                DeadlineOn = deadline is null ? string.Empty
                    : TimeZoneInfo.ConvertTime(deadline.DueAt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Version = row.Version.ToString(CultureInfo.InvariantCulture),
            };

            return new EditableListing(l.Id, new OrganisationId(l.OrgId), ParseStatus(l.Status), l.Slug, l.ReviewNote, l.UpdatedAt, l.ReviewedAt, form);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a draft (<paramref name="listingId"/> null) or saves an edit. Nothing is written
    /// unless the whole form is valid, the sites are the organisation's own, the terms are this
    /// tenant's, and nobody saved the listing since the editor opened it.
    /// </summary>
    public async Task<SaveOutcome> SaveAsync(
        OrganisationId organisation,
        Guid? listingId,
        ListingForm form,
        UserId actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        (ParsedListing? parsed, IReadOnlyDictionary<string, string[]> errors) = ListingFormParser.Parse(form);
        if (parsed is null)
        {
            return SaveOutcome.Invalid(errors);
        }

        Dictionary<string, string[]> references = await CheckReferencesAsync(organisation, parsed, cancellationToken).ConfigureAwait(false);
        if (references.Count > 0)
        {
            return SaveOutcome.Invalid(references);
        }

        try
        {
            return await db.InTransactionAsync(async ct =>
            {
                ListingRecord listing;
                if (listingId is { } id)
                {
                    ListingRecord? existing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
                    if (existing is null || existing.OrgId != organisation.Value)
                    {
                        return SaveOutcome.Refused("That listing does not belong to this organisation.");
                    }

                    ListingStatus status = ParseStatus(existing.Status);
                    if (!IsEditable(status))
                    {
                        return SaveOutcome.Refused(status == ListingStatus.PendingReview
                            ? "This listing is waiting for review. Withdraw it from review to edit it."
                            : $"A listing that is {status.ToDbValue().Replace('_', ' ')} cannot be edited.");
                    }

                    // Optimistic concurrency on xmin: the save fails if the row moved on.
                    if (!uint.TryParse(form.Version, NumberStyles.None, CultureInfo.InvariantCulture, out uint version))
                    {
                        return SaveOutcome.Refused("The form is missing its version. Reload the listing and try again.");
                    }

                    db.Entry(existing).Property<uint>("xmin").OriginalValue = version;
                    listing = existing;
                }
                else
                {
                    listing = new ListingRecord
                    {
                        Id = UuidV7.NewId(),
                        TenantId = tenant.TenantId.Value,
                        OrgId = organisation.Value,
                        Kind = parsed.Kind.ToDbValue(),
                        Slug = await UniqueSlugAsync(parsed.Title, ct).ConfigureAwait(false),
                        Title = parsed.Title,
                        Summary = parsed.Summary,
                        Status = ListingStatus.Draft.ToDbValue(),
                        CreatedBy = actor.Value,
                    };
                    db.Listings.Add(listing);
                }

                Apply(listing, parsed);
                await ReplaceChildrenAsync(listing, parsed, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await db.ScheduleProjectionAsync(tenant.TenantId, listing.Id, ct).ConfigureAwait(false);

                return new SaveOutcome(listing.Id, new Dictionary<string, string[]>(), null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SaveOutcome.Refused(
                "Someone else saved this listing after you opened it, so your changes were not saved. " +
                "Reload it to see theirs, then make your changes again.");
        }
    }

    /// <summary>Moves a listing between states, enforcing <see cref="ListingLifecycle"/>.</summary>
    public async Task<Result> TransitionAsync(
        Guid listingId,
        ListingStatus to,
        ListingLifecycle.Mover mover,
        OrganisationId? actingOrganisation,
        string? reason,
        UserId actor,
        CancellationToken cancellationToken = default)
    {
        ListingRecord? snapshot = await db.InTransactionAsync(
            ct => db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct), cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error.NotFound("listing.not_found", "That listing does not exist.");
        }

        if (mover == ListingLifecycle.Mover.Provider && actingOrganisation?.Value != snapshot.OrgId)
        {
            return Error.Forbidden("listing.not_yours", "That listing belongs to another organisation.");
        }

        OrganisationStatus? orgStatus = await directory.GetStatusAsync(new OrganisationId(snapshot.OrgId), cancellationToken).ConfigureAwait(false);
        bool passes = await PassesValidationAsync(snapshot, cancellationToken).ConfigureAwait(false);

        Result allowed = ListingLifecycle.CanTransition(ParseStatus(snapshot.Status), to, mover, new ListingLifecycle.TransitionContext
        {
            Reason = reason,
            OrganisationApproved = orgStatus == OrganisationStatus.Approved,
            PassesValidation = passes,
        });
        if (!allowed.IsSuccess)
        {
            return allowed;
        }

        return await db.InTransactionAsync(async ct =>
        {
            ListingRecord listing = await db.Listings.FirstAsync(l => l.Id == listingId, ct).ConfigureAwait(false);

            // Re-checked under the write: a concurrent move must not be applied on top of this one.
            if (listing.Status != snapshot.Status)
            {
                return Result.Failure(Error.Conflict("listing.moved", "The listing changed state while you were looking at it. Reload and try again."));
            }

            DateTimeOffset now = clock.UtcNow;
            listing.Status = to.ToDbValue();
            switch (to)
            {
                case ListingStatus.PendingReview:
                    listing.SubmittedAt = now;
                    listing.SubmittedBy = actor.Value;
                    break;
                case ListingStatus.Published:
                    listing.PublishedAt = now;
                    listing.UnpublishedAt = null;
                    listing.ReviewedAt = now;
                    listing.ReviewedBy = actor.Value;
                    listing.ReviewNote = null;
                    break;
                case ListingStatus.ChangesRequested:
                case ListingStatus.Rejected:
                    listing.ReviewedAt = now;
                    listing.ReviewedBy = actor.Value;
                    listing.ReviewNote = reason!.Trim();
                    break;
                case ListingStatus.Unpublished:
                    // ck_listing_published: published_at exists exactly while published.
                    listing.PublishedAt = null;
                    listing.UnpublishedAt = now;
                    listing.ReviewNote = reason!.Trim();
                    if (mover == ListingLifecycle.Mover.Organiser)
                    {
                        listing.ReviewedAt = now;
                        listing.ReviewedBy = actor.Value;
                    }

                    break;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.ScheduleProjectionAsync(tenant.TenantId, listing.Id, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An organiser has looked at a live listing's changes and is content with them (D15).</summary>
    public Task<Result> MarkReviewedAsync(Guid listingId, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ListingRecord? listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
            if (listing is null)
            {
                return Result.Failure(Error.NotFound("listing.not_found", "That listing does not exist."));
            }

            if (listing.Status != ListingStatus.Published.ToDbValue())
            {
                return Result.Failure(Error.Conflict("listing.not_published", "Only a published listing is reviewed this way."));
            }

            listing.ReviewedAt = clock.UtcNow;
            listing.ReviewedBy = actor.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<IReadOnlyList<ListingRow>> ListForOrganisationAsync(OrganisationId organisation, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ListingRow>>(async ct =>
        {
            List<ListingRecord> rows = await db.Listings.AsNoTracking()
                .Where(l => l.OrgId == organisation.Value)
                .OrderBy(l => l.Title).ThenBy(l => l.Slug)
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. rows.Select(ToRow)];
        }, cancellationToken);

    /// <summary>
    /// Listings submitted for review, oldest first, and published listings edited since an
    /// organiser last looked (D15), most recently changed first.
    /// </summary>
    public Task<ListingsForReview> ReviewQueueAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string pending = ListingStatus.PendingReview.ToDbValue();
            string published = ListingStatus.Published.ToDbValue();

            List<ListingRecord> awaiting = await db.Listings.AsNoTracking()
                .Where(l => l.Status == pending)
                .OrderBy(l => l.SubmittedAt).ThenBy(l => l.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            List<ListingRecord> changed = await db.Listings.AsNoTracking()
                .Where(l => l.Status == published && (l.ReviewedAt == null || l.UpdatedAt > l.ReviewedAt))
                .OrderByDescending(l => l.UpdatedAt)
                .Take(200)
                .ToListAsync(ct).ConfigureAwait(false);

            return new ListingsForReview([.. awaiting.Select(ToRow)], [.. changed.Select(ToRow)]);
        }, cancellationToken);

    /// <summary>The next sessions a form's schedule produces, or null when the schedule is not valid yet.</summary>
    public IReadOnlyList<Occurrence>? PreviewSessions(ListingForm form, int count)
    {
        (ParsedListing? parsed, _) = ListingFormParser.Parse(form);
        return parsed is null
            ? null
            : ListingFormParser.Preview(parsed, Zone, clock.TodayIn(TimeZoneInfo.FindSystemTimeZoneById(Zone)), count);
    }

    private async Task<Dictionary<string, string[]>> CheckReferencesAsync(OrganisationId organisation, ParsedListing parsed, CancellationToken ct)
    {
        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        if (parsed.SiteIds.Count > 0)
        {
            IReadOnlyList<SiteSummary> sites = await directory.GetSitesAsync(parsed.SiteIds, ct).ConfigureAwait(false);
            if (sites.Count != parsed.SiteIds.Count || sites.Any(s => s.OrganisationId != organisation || !s.IsActive))
            {
                errors[nameof(ListingForm.SiteIds)] = ["Choose from this organisation's active sites."];
            }
        }

        Guid[] termIds = [.. parsed.TermIds, .. parsed.AccommodationIds];
        IReadOnlyList<TermSummary> terms = await taxonomy.GetTermsAsync(termIds, "en", ct).ConfigureAwait(false);
        Dictionary<Guid, TermSummary> byId = terms.ToDictionary(t => t.Id);

        if (parsed.TermIds.Any(id => !byId.TryGetValue(id, out TermSummary? t) || t.Vocabulary is not ("category" or "interest")))
        {
            errors[nameof(ListingForm.TermIds)] = ["Choose categories and interests from the list."];
        }
        else if (!parsed.TermIds.Any(id => byId[id].Vocabulary == "category"))
        {
            errors[nameof(ListingForm.TermIds)] = ["Choose at least one category, so families can find this by topic."];
        }

        if (parsed.AccommodationIds.Any(id => !byId.TryGetValue(id, out TermSummary? t) || t.Vocabulary != "accommodation"))
        {
            errors[nameof(ListingForm.AccommodationIds)] = ["Choose accommodations from the list."];
        }

        return errors;
    }

    private static void Apply(ListingRecord listing, ParsedListing p)
    {
        listing.Kind = p.Kind.ToDbValue();
        listing.Title = p.Title;
        listing.Summary = p.Summary;
        listing.Description = p.Description;
        listing.PrimaryLanguage = p.PrimaryLanguage;
        listing.AgeMin = p.AgeMin;
        listing.AgeMax = p.AgeMax;
        listing.GradeMin = p.GradeMin;
        listing.GradeMax = p.GradeMax;
        listing.Modality = p.Modality.ToDbValue();
        listing.VirtualUrl = p.VirtualUrl;
        listing.IsDropIn = p.IsDropIn;
        listing.MealsProvided = p.MealsProvided;
        listing.TransportProvided = p.TransportProvided;
        listing.TransportNote = p.TransportNote;
        listing.CostModel = p.CostModel.ToDbValue();
        listing.CostCentsMin = p.CostCentsMin;
        listing.CostCentsMax = p.CostCentsMax;
        listing.SlidingScaleNote = p.SlidingScaleNote;
        listing.RegistrationMode = p.RegistrationMode.ToDbValue();

        // A changed link has not been checked yet; an unchanged one keeps its verdict.
        if (!string.Equals(listing.ExternalUrl, p.ExternalUrl, StringComparison.Ordinal))
        {
            listing.ExternalUrl = p.ExternalUrl;
            listing.ExternalUrlState = "unchecked";
            listing.ExternalUrlCheckedAt = null;
        }

        listing.CapacityTotal = p.CapacityTotal;
        listing.WaitlistEnabled = p.RegistrationMode == RegistrationMode.Builtin;
        listing.ContactEmail = p.ContactEmail;
        listing.ContactPhone = p.ContactPhone;
        listing.AccessibilityNote = p.AccessibilityNote;

        // An event stops being listed the day after it ends.
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        listing.ExpiresAt = p.Kind == ListingKind.Event && p.EndsOn is { } last
            ? Local(zone, last.AddDays(1), TimeOnly.MinValue)
            : null;
    }

    private async Task ReplaceChildrenAsync(ListingRecord listing, ParsedListing p, CancellationToken ct)
    {
        Guid id = listing.Id;
        await db.ListingSites.Where(x => x.ListingId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ListingTerms.Where(x => x.ListingId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ListingAccommodations.Where(x => x.ListingId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ListingLanguages.Where(x => x.ListingId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ListingDeadlines.Where(x => x.ListingId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        db.ListingSites.AddRange(p.SiteIds.Select(s => new ListingSiteRecord { ListingId = id, SiteId = s }));
        db.ListingTerms.AddRange(p.TermIds.Select((t, i) => new ListingTermRecord { ListingId = id, TermId = t, IsPrimary = i == 0 }));
        db.ListingAccommodations.AddRange(p.AccommodationIds.Select(a => new ListingAccommodationRecord { ListingId = id, TermId = a }));
        db.ListingLanguages.AddRange(p.Languages.Select(code => new ListingLanguageRecord { ListingId = id, LanguageCode = code }));

        if (p.DeadlineKind is { } kind && p.DeadlineOn is { } due)
        {
            db.ListingDeadlines.Add(new ListingDeadlineRecord
            {
                Id = UuidV7.NewId(),
                ListingId = id,
                DeadlineKind = kind,
                DueAt = Local(TimeZoneInfo.FindSystemTimeZoneById(Zone), due, new TimeOnly(23, 59)),
            });
        }

        // The editor manages one session. A listing with more (an import, say) keeps the
        // others; the editor edits the earliest.
        ListingSessionRecord? session = await db.Sessions
            .Where(s => s.ListingId == id && !s.IsCancelled)
            .OrderBy(s => s.StartsOn)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            session = new ListingSessionRecord { Id = UuidV7.NewId(), TenantId = listing.TenantId, ListingId = id, Timezone = Zone };
            db.Sessions.Add(session);
        }

        session.SiteId = p.SiteIds.Count > 0 ? p.SiteIds[0] : null;
        session.StartsOn = p.StartsOn;
        session.EndsOn = p.EndsOn;
        session.StartTime = p.StartTime;
        session.EndTime = p.EndTime;
        session.Rrule = p.Rrule;
        session.IsAllDay = false;
    }

    private async Task<bool> PassesValidationAsync(ListingRecord l, CancellationToken ct)
    {
        int siteCount = await db.InTransactionAsync(
            c => db.ListingSites.CountAsync(x => x.ListingId == l.Id, c), ct).ConfigureAwait(false);

        return ListingValidator.Validate(new ListingDraft
        {
            Kind = Parse<ListingKind>(l.Kind, k => k.ToDbValue()),
            Title = l.Title,
            Summary = l.Summary,
            AgeMin = l.AgeMin,
            AgeMax = l.AgeMax,
            GradeMin = l.GradeMin,
            GradeMax = l.GradeMax,
            Modality = Parse<Modality>(l.Modality, m => m.ToDbValue()),
            VirtualUrl = l.VirtualUrl,
            CostModel = Parse<CostModel>(l.CostModel, c => c.ToDbValue()),
            CostCentsMin = l.CostCentsMin,
            CostCentsMax = l.CostCentsMax,
            RegistrationMode = Parse<RegistrationMode>(l.RegistrationMode, r => r.ToDbValue()),
            ExternalUrl = l.ExternalUrl,
            CapacityTotal = l.CapacityTotal,
            SiteCount = siteCount,
        }).IsSuccess;
    }

    private async Task<string> UniqueSlugAsync(string title, CancellationToken ct)
    {
        string slug = Slug.From(title);
        string candidate = slug;
        for (int i = 2; await db.Listings.AnyAsync(l => l.Slug == candidate, ct).ConfigureAwait(false); i++)
        {
            candidate = $"{slug}-{i.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    private static ListingRow ToRow(ListingRecord l) => new(
        l.Id, new OrganisationId(l.OrgId), l.Slug, l.Title,
        Parse<ListingKind>(l.Kind, k => k.ToDbValue()), ParseStatus(l.Status),
        l.UpdatedAt, l.SubmittedAt, l.ReviewedAt, l.ReviewNote);

    internal static ListingStatus ParseStatus(string value) => Parse<ListingStatus>(value, s => s.ToDbValue());

    private static T Parse<T>(string value, Func<T, string> toDb) where T : struct, Enum
    {
        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(toDb(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"'{value}' is not a valid {typeof(T).Name}.");
    }

    private static DateTimeOffset Local(TimeZoneInfo zone, DateOnly day, TimeOnly time)
    {
        DateTime local = day.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Text(short? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Dollars(int? cents) => cents is { } c
        ? (c % 100 == 0 ? (c / 100).ToString(CultureInfo.InvariantCulture) : (c / 100m).ToString("0.00", CultureInfo.InvariantCulture))
        : string.Empty;

    private static List<string> DaysOf(string rrule)
    {
        int at = rrule.IndexOf("BYDAY=", StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }

        string list = rrule[(at + 6)..].Split(';')[0];
        return [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries)];
    }
}
