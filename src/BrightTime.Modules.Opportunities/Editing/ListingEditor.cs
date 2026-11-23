using System.Globalization;
using BrightTime.Infrastructure.Auditing;
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
using BrightTime.SharedKernel.Text;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>
/// An edit to what a published listing says, waiting for an organiser (D101), or sent back by one
/// with a note.
/// </summary>
public sealed record PendingEdit(bool Returned, DateTimeOffset SavedAt, string? ReviewNote, DateTimeOffset? ReviewedAt);

/// <summary>A pending edit and what it would change, field by field, against the live listing.</summary>
public sealed record PendingEditView(PendingEdit Edit, IReadOnlyList<FieldChange> Changes);

/// <summary>
/// A listing opened in the editor: its form, and the state around it. With a pending edit, the
/// form is that edit's wording with the live schedule.
/// </summary>
public sealed record EditableListing(
    Guid Id,
    OrganisationId OrganisationId,
    ListingStatus Status,
    string Slug,
    string? ReviewNote,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReviewedAt,
    ListingForm Form,
    PendingEdit? Pending = null)
{
    /// <summary>Published and edited since an organiser last looked (D15).</summary>
    public bool ChangedSinceReview => Status == ListingStatus.Published && (ReviewedAt is null || UpdatedAt > ReviewedAt);
}

/// <summary>What a save did: the listing's id, or why nothing was written.</summary>
public sealed record SaveOutcome(Guid? ListingId, IReadOnlyDictionary<string, Phrase[]> Errors, Phrase? Problem)
{
    public bool Saved => ListingId is not null && Errors.Count == 0 && Problem is null;

    /// <summary>The save changed what a published listing says, so that waits for an organiser (D101).</summary>
    public bool Held { get; init; }

    internal static SaveOutcome Invalid(IReadOnlyDictionary<string, Phrase[]> errors) => new(null, errors, null);

    internal static SaveOutcome Refused(Phrase problem) => new(null, new Dictionary<string, Phrase[]>(), problem);
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
    string? ReviewNote,
    PendingEdit? Edit = null);

/// <summary>A published listing whose edit waits for an organiser, and when it was saved.</summary>
public sealed record PendingEditRow(ListingRow Listing, DateTimeOffset SavedAt);

/// <summary>The listings waiting for an organiser.</summary>
public sealed record ListingsForReview(IReadOnlyList<ListingRow> AwaitingReview, IReadOnlyList<ListingRow> ChangedSinceReview, IReadOnlyList<PendingEditRow> EditsWaiting);

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
/// An edit to a published listing's schedule goes live and puts the listing in the organiser's
/// "changed since review" lane (D15). An edit to what it says waits for an organiser as a pending
/// edit, while families see the approved version (D101).
/// </para>
/// </remarks>
public sealed class ListingEditor(
    OpportunitiesDbContext db,
    IDirectoryQueries directory,
    ITaxonomyQueries taxonomy,
    ITenantContext tenant,
    IClock clock,
    IAuditWriter audit)
{
    public const string Zone = "America/New_York";

    private static readonly ListingStatus[] Editable =
        [ListingStatus.Draft, ListingStatus.ChangesRequested, ListingStatus.Published, ListingStatus.Unpublished];

    public static bool IsEditable(ListingStatus status) => Editable.Contains(status);

    public async Task<EditableListing?> LoadAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(async ct =>
        {
            (ListingRecord Listing, ListingForm Form)? read = await ReadFormAsync(listingId, ct).ConfigureAwait(false);
            if (read is not { } r)
            {
                return null;
            }

            ListingRecord l = r.Listing;
            ListingStatus status = ParseStatus(l.Status);
            ListingForm form = r.Form;
            PendingEdit? pending = null;
            if (status == ListingStatus.Published && await PendingAsync(l.Id, ct).ConfigureAwait(false) is { } edit
                && Proposed(edit.Record, r.Form) is { } proposed)
            {
                form = proposed;
                form.Version = VersionOf(r.Form.Version, edit.Version);
                pending = View(edit.Record);
            }

            return new EditableListing(l.Id, new OrganisationId(l.OrgId), status, l.Slug, l.ReviewNote, l.UpdatedAt, l.ReviewedAt, form, pending);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A listing's pending edit and its row version, untracked. Runs inside the caller's transaction.</summary>
    private async Task<(ListingPendingEditRecord Record, uint Version)?> PendingAsync(Guid listingId, CancellationToken ct)
    {
        var row = await db.ListingPendingEdits.AsNoTracking()
            .Where(e => e.ListingId == listingId)
            .Select(e => new { Edit = e, Version = EF.Property<uint>(e, "xmin") })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : (row.Edit, row.Version);
    }

    /// <summary>
    /// The listing as a pending edit would make it: the edit's wording with the live schedule, or
    /// the whole edit where it holds its schedule too. Null if the snapshot can't be read.
    /// </summary>
    private static ListingForm? Proposed(ListingPendingEditRecord edit, ListingForm live) =>
        ListingRevisions.Deserialise(edit.Snapshot) is { } saved
            ? (edit.HoldsSchedule ? saved : ListingRevisions.Merge(saved, live))
            : null;

    private static PendingEdit View(ListingPendingEditRecord edit) =>
        new(edit.State == ListingPendingEditRecord.Returned, edit.SavedAt, edit.ReviewNote, edit.ReviewedAt);

    /// <summary>
    /// The form's version: the listing's row version, and the pending edit's after a slash when
    /// there is one, so a save is refused if either moved on since the editor opened.
    /// </summary>
    private static string VersionOf(string listing, uint? pending) =>
        pending is { } p ? listing + "/" + p.ToString(CultureInfo.InvariantCulture) : listing;

    private static bool TryParseVersion(string text, out uint listing, out uint? pending)
    {
        pending = null;
        string[] parts = (text ?? string.Empty).Split('/');
        if (parts.Length > 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out listing))
        {
            listing = 0;
            return false;
        }

        if (parts.Length == 2)
        {
            if (!uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint p))
            {
                return false;
            }

            pending = p;
        }

        return true;
    }

    /// <summary>The listing and its editor form, with the row version in the form. Runs inside the caller's transaction.</summary>
    private async Task<(ListingRecord Listing, ListingForm Form)?> ReadFormAsync(Guid listingId, CancellationToken ct)
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
            Interval = session?.Rrule is { } rule ? IntervalOf(rule) : "1",
            SkipDates = session is null ? string.Empty
                : string.Join('\n', session.Exdates.Order().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            ShowFrom = l.PublishAt is { } show ? Day(TimeZoneInfo.ConvertTime(show, zone)) : string.Empty,
            TakeDownAfter = TakeDownText(l, session, zone),
            Version = row.Version.ToString(CultureInfo.InvariantCulture),
        };

        return (l, form);
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

        (ParsedListing? parsed, IReadOnlyDictionary<string, Phrase[]> errors) = ListingFormParser.Parse(form);
        if (parsed is null)
        {
            return SaveOutcome.Invalid(errors);
        }

        Dictionary<string, Phrase[]> references = await CheckReferencesAsync(organisation, parsed, cancellationToken).ConfigureAwait(false);
        if (references.Count > 0)
        {
            return SaveOutcome.Invalid(references);
        }

        try
        {
            return await db.InTransactionAsync(async ct =>
            {
                ListingRecord listing;
                string? from = null;
                bool held = false;
                if (listingId is { } id)
                {
                    ListingRecord? existing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
                    if (existing is null || existing.OrgId != organisation.Value)
                    {
                        return SaveOutcome.Refused(Phrase.Of("editor.refused.not_this_organisation"));
                    }

                    ListingStatus status = ParseStatus(existing.Status);
                    if (!IsEditable(status))
                    {
                        return SaveOutcome.Refused(status == ListingStatus.PendingReview
                            ? Phrase.Of("editor.refused.pending_review")
                            : Phrase.Of("editor.refused.not_editable", ("status", ListingLifecycle.StatusPhrase(status))));
                    }

                    // Optimistic concurrency on xmin: the save fails if the row moved on. Checked here
                    // too, because a held edit may write nothing to the listing's own row.
                    if (!TryParseVersion(form.Version, out uint version, out uint? pendingVersion))
                    {
                        return SaveOutcome.Refused(Phrase.Of("editor.refused.missing_version"));
                    }

                    ListingPendingEditRecord? pending = await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == id, ct).ConfigureAwait(false);
                    uint? currentPending = pending is null ? null : db.Entry(pending).Property<uint>("xmin").CurrentValue;
                    if (db.Entry(existing).Property<uint>("xmin").CurrentValue != version || currentPending != pendingVersion)
                    {
                        return SaveOutcome.Refused(Phrase.Of("editor.refused.concurrent_save"));
                    }

                    db.Entry(existing).Property<uint>("xmin").OriginalValue = version;
                    listing = existing;
                    from = existing.Status;
                    if (status == ListingStatus.Published)
                    {
                        held = await SavePublishedAsync(existing, form, parsed, pending, actor, ct).ConfigureAwait(false);
                    }
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

                if (from != ListingStatus.Published.ToDbValue())
                {
                    Apply(listing, parsed);
                    await ReplaceChildrenAsync(listing, parsed, ct).ConfigureAwait(false);
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await db.ScheduleProjectionAsync(tenant.TenantId, listing.Id, ct).ConfigureAwait(false);
                await audit.RecordInTransactionAsync(db, new AuditAccess
                {
                    Action = from is null ? "listing.created" : held ? EditHeld : "listing.updated",
                    ActorId = actor,
                    ActorOrgId = organisation,
                    SubjectKind = "listing",
                    SubjectIds = [listing.Id],
                    Detail = new Dictionary<string, string> { ["status"] = listing.Status },
                }, ct).ConfigureAwait(false);

                return new SaveOutcome(listing.Id, new Dictionary<string, Phrase[]>(), null) { Held = held };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SaveOutcome.Refused(Phrase.Of("editor.refused.concurrent_save"));
        }
    }

    public const string EditHeld = "listing.edit_held";

    /// <summary>
    /// Saves an edit to a published listing, in the caller's transaction (D101). A change to what
    /// it says waits as the listing's pending edit, and the schedule's own changes go live; with no
    /// change to what it says, the whole edit goes live as before, and any pending edit is dropped,
    /// since the provider has put the wording back.
    /// </summary>
    /// <returns>Whether anything waits for an organiser.</returns>
    private async Task<bool> SavePublishedAsync(ListingRecord listing, ListingForm form, ParsedListing parsed, ListingPendingEditRecord? pending, UserId actor, CancellationToken ct)
    {
        ListingForm live = (await ReadFormAsync(listing.Id, ct).ConfigureAwait(false))!.Value.Form;
        if (ListingRevisions.CompareWording(live, form).Count == 0)
        {
            if (pending is not null)
            {
                db.ListingPendingEdits.Remove(pending);
            }

            Apply(listing, parsed);
            await ReplaceChildrenAsync(listing, parsed, ct).ConfigureAwait(false);
            return false;
        }

        // The new schedule with the approved wording. If that isn't a valid listing (a new kind
        // with a schedule only the new kind allows, say), the schedule waits with the wording.
        ListingForm scheduleOnly = ListingRevisions.Merge(live, form);
        (ParsedListing? schedule, _) = ListingFormParser.Parse(scheduleOnly);
        if (schedule is not null && ListingRevisions.Compare(live, scheduleOnly).Count > 0)
        {
            Apply(listing, schedule);
            await ReplaceChildrenAsync(listing, schedule, ct).ConfigureAwait(false);
        }

        if (pending is null)
        {
            pending = new ListingPendingEditRecord
            {
                ListingId = listing.Id,
                TenantId = listing.TenantId,
                Snapshot = string.Empty,
                State = ListingPendingEditRecord.Waiting,
            };
            db.ListingPendingEdits.Add(pending);
        }

        pending.Snapshot = ListingRevisions.Serialise(form);
        pending.HoldsSchedule = schedule is null;
        pending.State = ListingPendingEditRecord.Waiting;
        pending.SavedAt = clock.UtcNow;
        pending.SavedBy = actor.Value;
        return true;
    }

    /// <summary>
    /// What a published listing's pending edit would change, against the live listing, with sites
    /// and terms named in <paramref name="locale"/>. Null when nothing waits.
    /// </summary>
    public Task<PendingEditView?> PendingEditAsync(Guid listingId, string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await PendingAsync(listingId, ct).ConfigureAwait(false) is not { } edit
                || await ReadFormAsync(listingId, ct).ConfigureAwait(false) is not { } live
                || Proposed(edit.Record, live.Form) is not { } proposed)
            {
                return null;
            }

            IReadOnlyList<FieldChange> changes = ListingRevisions.Compare(live.Form, proposed);
            return new PendingEditView(View(edit.Record), await NameIdsAsync(changes, locale, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// An organiser approves a published listing's pending edit: it goes live, as if saved now,
    /// and the listing counts as reviewed. Refused if the edit no longer makes a valid listing, or
    /// names a site or term that's gone; the organiser sends it back instead.
    /// </summary>
    public Task<Result> ApproveEditAsync(Guid listingId, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ListingRecord? listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
            if (listing is null)
            {
                return Result.Failure(Error.NotFound("listing.not_found", "That listing does not exist."));
            }

            ListingPendingEditRecord? pending = await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listingId, ct).ConfigureAwait(false);
            if (listing.Status != ListingStatus.Published.ToDbValue() || pending is not { State: ListingPendingEditRecord.Waiting })
            {
                return Result.Failure(Error.Conflict("listing.no_pending_edit", "No changes to this listing are waiting for approval."));
            }

            ListingForm live = (await ReadFormAsync(listingId, ct).ConfigureAwait(false))!.Value.Form;
            ParsedListing? parsed = Proposed(pending, live) is { } proposed ? ListingFormParser.Parse(proposed).Listing : null;
            if (parsed is null)
            {
                return Result.Failure(Error.Precondition("listing.edit_invalid", "These changes no longer make a listing that can be saved. Send them back."));
            }

            if ((await CheckReferencesAsync(new OrganisationId(listing.OrgId), parsed, ct).ConfigureAwait(false)).Count > 0)
            {
                return Result.Failure(Error.Precondition("listing.edit_references", "A site or topic in these changes is no longer available. Send them back."));
            }

            Apply(listing, parsed);
            await ReplaceChildrenAsync(listing, parsed, ct).ConfigureAwait(false);
            db.ListingPendingEdits.Remove(pending);
            listing.ReviewedAt = clock.UtcNow;
            listing.ReviewedBy = actor.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await RecordRevisionAsync(listing, ListingRevisions.Reviewed, actor, ct).ConfigureAwait(false);
            await ListingDecisions.BookAsync(
                db, directory, tenant, listing.OrgId, listing.Id, ListingDecisionNotice.EditApproved, pending.SavedBy, listing.ReviewedAt.Value, ct).ConfigureAwait(false);
            await db.ScheduleProjectionAsync(tenant.TenantId, listing.Id, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.edit_approved",
                ActorId = actor,
                SubjectKind = "listing",
                SubjectIds = [listing.Id],
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>An organiser sends a pending edit back with a note; the listing stays as approved, and the edit stays with the provider.</summary>
    public Task<Result> ReturnEditAsync(Guid listingId, string? note, UserId actor, CancellationToken cancellationToken = default)
    {
        string? trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is null)
        {
            return Task.FromResult(Result.Failure(Error.Validation("listing.edit_note_required", "Say what needs changing, so the provider can put it right.")));
        }

        return db.InTransactionAsync(async ct =>
        {
            ListingPendingEditRecord? pending = await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listingId, ct).ConfigureAwait(false);
            if (pending is not { State: ListingPendingEditRecord.Waiting })
            {
                return Result.Failure(Error.Conflict("listing.no_pending_edit", "No changes to this listing are waiting for approval."));
            }

            pending.State = ListingPendingEditRecord.Returned;
            pending.ReviewNote = trimmed;
            pending.ReviewedAt = clock.UtcNow;
            pending.ReviewedBy = actor.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            Guid orgId = await db.Listings.AsNoTracking().Where(l => l.Id == listingId).Select(l => l.OrgId).FirstAsync(ct).ConfigureAwait(false);
            await ListingDecisions.BookAsync(
                db, directory, tenant, orgId, listingId, ListingDecisionNotice.EditReturned, pending.SavedBy, pending.ReviewedAt.Value, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.edit_returned",
                ActorId = actor,
                SubjectKind = "listing",
                SubjectIds = [listingId],
                Reason = trimmed,
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);
    }

    /// <summary>The provider throws their pending edit away; the editor then shows the approved version.</summary>
    public Task<Result> DiscardEditAsync(OrganisationId organisation, Guid listingId, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ListingRecord? listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
            if (listing is null || listing.OrgId != organisation.Value)
            {
                return Result.Failure(Error.Forbidden("listing.not_yours", "That listing belongs to another organisation."));
            }

            ListingPendingEditRecord? pending = await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listingId, ct).ConfigureAwait(false);
            if (pending is null)
            {
                return Result.Failure(Error.Conflict("listing.no_pending_edit", "No changes to this listing are waiting for approval."));
            }

            db.ListingPendingEdits.Remove(pending);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.edit_discarded",
                ActorId = actor,
                ActorOrgId = organisation,
                SubjectKind = "listing",
                SubjectIds = [listingId],
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>
    /// Seasonal clone: a new draft with everything the original says, its schedule moved so the
    /// first session falls on <paramref name="startsOn"/>. Sessions, skipped days, deadlines and
    /// the show and take-down days all move by the same number of days.
    /// </summary>
    /// <returns>The new draft's id, or the reason there is none.</returns>
    public async Task<(Guid? ListingId, Phrase? Problem)> CloneAsync(
        OrganisationId organisation,
        Guid listingId,
        DateOnly startsOn,
        string? seasonLabel,
        UserId actor,
        CancellationToken cancellationToken = default)
    {
        string? label = string.IsNullOrWhiteSpace(seasonLabel) ? null : seasonLabel.Trim();
        if (label is { Length: > 60 })
        {
            return (null, Phrase.Of("editor.error.too_long", ("max", 60), ("length", label.Length)));
        }

        return await db.InTransactionAsync<(Guid?, Phrase?)>(async ct =>
        {
            ListingRecord? original = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct).ConfigureAwait(false);
            if (original is null || original.OrgId != organisation.Value)
            {
                return (null, Phrase.Of("editor.refused.not_this_organisation"));
            }

            List<ListingSessionRecord> sessions = await db.Sessions.AsNoTracking()
                .Where(s => s.ListingId == listingId && !s.IsCancelled).OrderBy(s => s.StartsOn).ToListAsync(ct).ConfigureAwait(false);
            int shift = sessions.Count > 0 ? startsOn.DayNumber - sessions[0].StartsOn.DayNumber : 0;
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
            DateTimeOffset? Move(DateTimeOffset? instant) => instant is { } at ? Shift(at, shift, zone) : null;

            Guid id = UuidV7.NewId();
            ListingRecord copy = new()
            {
                Id = id,
                TenantId = original.TenantId,
                OrgId = original.OrgId,
                Kind = original.Kind,
                Slug = await UniqueSlugAsync(label is null ? original.Title : $"{original.Title} {label}", ct).ConfigureAwait(false),
                Title = original.Title,
                Summary = original.Summary,
                Description = original.Description,
                PrimaryLanguage = original.PrimaryLanguage,
                Status = ListingStatus.Draft.ToDbValue(),
                AgeMin = original.AgeMin,
                AgeMax = original.AgeMax,
                GradeMin = original.GradeMin,
                GradeMax = original.GradeMax,
                AudienceNote = original.AudienceNote,
                Modality = original.Modality,
                VirtualUrl = original.VirtualUrl,
                IsDropIn = original.IsDropIn,
                MealsProvided = original.MealsProvided,
                TransportProvided = original.TransportProvided,
                TransportNote = original.TransportNote,
                CostModel = original.CostModel,
                CostCentsMin = original.CostCentsMin,
                CostCentsMax = original.CostCentsMax,
                SlidingScaleNote = original.SlidingScaleNote,
                RegistrationMode = original.RegistrationMode,
                ExternalUrl = original.ExternalUrl,
                CapacityTotal = original.CapacityTotal,
                WaitlistEnabled = original.WaitlistEnabled,
                PublishAt = Move(original.PublishAt),
                ExpiresAt = Move(original.ExpiresAt),
                ClonedFromId = original.Id,
                SeasonLabel = label ?? original.SeasonLabel,
                HeroMediaId = original.HeroMediaId,
                ContactEmail = original.ContactEmail,
                ContactPhone = original.ContactPhone,
                AccessibilityNote = original.AccessibilityNote,
                CreatedBy = actor.Value,
            };
            db.Listings.Add(copy);

            db.ListingSites.AddRange((await db.ListingSites.AsNoTracking().Where(x => x.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => new ListingSiteRecord { ListingId = id, SiteId = x.SiteId }));
            db.ListingTerms.AddRange((await db.ListingTerms.AsNoTracking().Where(x => x.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => new ListingTermRecord { ListingId = id, TermId = x.TermId, IsPrimary = x.IsPrimary }));
            db.ListingAccommodations.AddRange((await db.ListingAccommodations.AsNoTracking().Where(x => x.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => new ListingAccommodationRecord { ListingId = id, TermId = x.TermId }));
            db.ListingLanguages.AddRange((await db.ListingLanguages.AsNoTracking().Where(x => x.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => new ListingLanguageRecord { ListingId = id, LanguageCode = x.LanguageCode }));
            db.ListingTranslations.AddRange((await db.ListingTranslations.AsNoTracking().Where(x => x.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => new ListingTranslationRecord
                {
                    ListingId = id,
                    Locale = x.Locale,
                    Title = x.Title,
                    Summary = x.Summary,
                    Description = x.Description,
                    TranslationSource = x.TranslationSource,
                    TranslatedAt = x.TranslatedAt,
                    TranslatedBy = x.TranslatedBy,
                    ReviewedAt = x.ReviewedAt,
                }));
            db.ListingDeadlines.AddRange((await db.ListingDeadlines.AsNoTracking().Where(x => x.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => new ListingDeadlineRecord { Id = UuidV7.NewId(), ListingId = id, DeadlineKind = x.DeadlineKind, DueAt = Shift(x.DueAt, shift, zone), Label = x.Label }));
            db.Sessions.AddRange(sessions.Select(s => new ListingSessionRecord
            {
                Id = UuidV7.NewId(),
                TenantId = s.TenantId,
                ListingId = id,
                SiteId = s.SiteId,
                Label = s.Label,
                Timezone = s.Timezone,
                StartsOn = AlignedStart(s.Rrule, s.StartsOn.AddDays(shift)),
                EndsOn = s.EndsOn?.AddDays(shift),
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                Rrule = s.Rrule,
                Exdates = [.. s.Exdates.Select(d => d.AddDays(shift))],
                IsAllDay = s.IsAllDay,
            }));

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.ScheduleProjectionAsync(tenant.TenantId, id, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.cloned",
                ActorId = actor,
                ActorOrgId = organisation,
                SubjectKind = "listing",
                SubjectIds = [id, original.Id],
                Detail = new Dictionary<string, string> { ["shift_days"] = shift.ToString(CultureInfo.InvariantCulture) },
            }, ct).ConfigureAwait(false);
            return (id, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an import's valid rows as one operation: a row whose external id this organisation
    /// already imported updates that listing, any other row creates a draft. A listing waiting
    /// for review, or past editing, is left alone and reported. An update to what a published
    /// listing says waits for an organiser, as the editor's does (D101).
    /// </summary>
    /// <remarks>
    /// The rows must already have passed <see cref="ListingFormParser"/>, and their sites and terms
    /// must be this organisation's and this tenant's: the importer resolves names and codes to ids
    /// only from those lists.
    /// </remarks>
    public Task<ImportOutcome> ImportAsync(
        OrganisationId organisation,
        string source,
        IReadOnlyList<(string ExternalId, ListingForm Form, ParsedListing Listing)> rows,
        UserId actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (source is not ("csv_import" or "hsds_import"))
        {
            throw new ArgumentException("An import is either a CSV or an HSDS import.", nameof(source));
        }

        return db.InTransactionAsync(async ct =>
        {
            string[] ids = [.. rows.Select(r => r.ExternalId)];
            Dictionary<string, ListingRecord> existing = await db.Listings
                .Where(l => l.OrgId == organisation.Value && l.Source == source && l.SourceRecordId != null && ids.Contains(l.SourceRecordId))
                .ToDictionaryAsync(l => l.SourceRecordId!, StringComparer.Ordinal, ct).ConfigureAwait(false);

            List<Guid> created = [];
            List<Guid> updated = [];
            List<string> skipped = [];
            int held = 0;
            foreach ((string externalId, ListingForm form, ParsedListing parsed) in rows)
            {
                if (existing.TryGetValue(externalId, out ListingRecord? listing))
                {
                    ListingStatus status = ParseStatus(listing.Status);
                    if (!IsEditable(status))
                    {
                        skipped.Add(externalId);
                        continue;
                    }

                    updated.Add(listing.Id);
                    if (status == ListingStatus.Published)
                    {
                        ListingPendingEditRecord? pending = await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listing.Id, ct).ConfigureAwait(false);
                        held += await SavePublishedAsync(listing, form, parsed, pending, actor, ct).ConfigureAwait(false) ? 1 : 0;
                        continue;
                    }

                    Apply(listing, parsed);
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
                        Source = source,
                        SourceRecordId = externalId,
                        CreatedBy = actor.Value,
                    };
                    Apply(listing, parsed);
                    db.Listings.Add(listing);
                    created.Add(listing.Id);

                    // Written now: a slug picked for this row must not be picked again for a
                    // later row in the same file, and its children reference it.
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                await ReplaceChildrenAsync(listing, parsed, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (Guid id in created.Concat(updated))
            {
                await db.ScheduleProjectionAsync(tenant.TenantId, id, ct).ConfigureAwait(false);
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.import_applied",
                ActorId = actor,
                ActorOrgId = organisation,
                SubjectKind = "listing",
                SubjectIds = [.. created, .. updated],
                Detail = new Dictionary<string, string>
                {
                    ["source"] = source,
                    ["created"] = created.Count.ToString(CultureInfo.InvariantCulture),
                    ["updated"] = updated.Count.ToString(CultureInfo.InvariantCulture),
                    ["skipped"] = skipped.Count.ToString(CultureInfo.InvariantCulture),
                    ["held"] = held.ToString(CultureInfo.InvariantCulture),
                },
            }, ct).ConfigureAwait(false);

            return new ImportOutcome(created.Count, updated.Count, skipped, held);
        }, cancellationToken);
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

            // A pending edit lives only while the listing is published (D101). Taken down, the
            // listing takes the edit's wording: nobody sees it, and it's reviewed before it's
            // published again. Archived, the edit goes with it.
            string? pendingEdit = null;
            Guid? author = listing.SubmittedBy;
            if (snapshot.Status == ListingStatus.Published.ToDbValue()
                && await db.ListingPendingEdits.FirstOrDefaultAsync(e => e.ListingId == listingId, ct).ConfigureAwait(false) is { } pending)
            {
                pendingEdit = "dropped";
                author = pending.SavedBy ?? author;
                if (to == ListingStatus.Unpublished
                    && Proposed(pending, (await ReadFormAsync(listingId, ct).ConfigureAwait(false))!.Value.Form) is { } proposed
                    && ListingFormParser.Parse(proposed).Listing is { } parsed)
                {
                    Apply(listing, parsed);
                    await ReplaceChildrenAsync(listing, parsed, ct).ConfigureAwait(false);
                    pendingEdit = "kept";
                }

                db.ListingPendingEdits.Remove(pending);
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

            // D15: what was submitted, and what an organiser looked at, so the next review is a diff.
            if (to == ListingStatus.PendingReview)
            {
                await RecordRevisionAsync(listing, ListingRevisions.Submitted, actor, ct).ConfigureAwait(false);
            }
            else if (mover == ListingLifecycle.Mover.Organiser && to is ListingStatus.Published or ListingStatus.ChangesRequested or ListingStatus.Rejected)
            {
                await RecordRevisionAsync(listing, ListingRevisions.Reviewed, actor, ct).ConfigureAwait(false);
            }

            // D102: an organiser's decision is emailed to whoever sent the listing in (or saved
            // the edit it carried) and to the organisation's owners and admins.
            if (mover == ListingLifecycle.Mover.Organiser
                && to is ListingStatus.Published or ListingStatus.ChangesRequested or ListingStatus.Rejected or ListingStatus.Unpublished)
            {
                await ListingDecisions.BookAsync(db, directory, tenant, listing.OrgId, listing.Id, listing.Status, author, now, ct).ConfigureAwait(false);
            }

            await db.ScheduleProjectionAsync(tenant.TenantId, listing.Id, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.status_changed",
                ActorId = actor,
                ActorOrgId = mover == ListingLifecycle.Mover.Provider ? actingOrganisation : null,
                SubjectKind = "listing",
                SubjectIds = [listing.Id],
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                Detail = pendingEdit is null
                    ? new Dictionary<string, string> { ["from"] = snapshot.Status, ["to"] = listing.Status }
                    : new Dictionary<string, string> { ["from"] = snapshot.Status, ["to"] = listing.Status, ["pending_edit"] = pendingEdit },
            }, ct).ConfigureAwait(false);
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
            await RecordRevisionAsync(listing, ListingRevisions.Reviewed, actor, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "listing.reviewed",
                ActorId = actor,
                SubjectKind = "listing",
                SubjectIds = [listing.Id],
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>
    /// What changed since an organiser last reviewed the listing: for a resubmission after
    /// changes were asked for, and for the "changed since review" lane (D15). Sites and terms
    /// are named in <paramref name="locale"/>. Null when the listing was never reviewed.
    /// </summary>
    public Task<ReviewDiff?> DiffSinceReviewAsync(Guid listingId, string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ListingRevisionRecord? last = await db.ListingRevisions.AsNoTracking()
                .Where(r => r.ListingId == listingId && r.Kind == ListingRevisions.Reviewed)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (last is null || ListingRevisions.Deserialise(last.Snapshot) is not { } before
                || await ReadFormAsync(listingId, ct).ConfigureAwait(false) is not { } current)
            {
                return null;
            }

            IReadOnlyList<FieldChange> changes = ListingRevisions.Compare(before, current.Form);
            return new ReviewDiff(last.CreatedAt, await NameIdsAsync(changes, locale, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Replaces site and term ids in a diff with their names; an id no longer known stays as it is.</summary>
    private async Task<IReadOnlyList<FieldChange>> NameIdsAsync(IReadOnlyList<FieldChange> changes, string locale, CancellationToken ct)
    {
        static Guid[] Ids(FieldChange c) =>
            [.. c.Before.Concat(c.After).Select(v => Guid.TryParse(v, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

        string[] termFields = [nameof(ListingForm.TermIds), nameof(ListingForm.AccommodationIds)];
        Guid[] termIds = [.. changes.Where(c => termFields.Contains(c.Field)).SelectMany(Ids).Distinct()];
        Guid[] siteIds = [.. changes.Where(c => c.Field == nameof(ListingForm.SiteIds)).SelectMany(Ids).Distinct()];

        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        if (termIds.Length > 0)
        {
            foreach (var t in await taxonomy.GetTermsAsync(termIds, locale, ct).ConfigureAwait(false))
            {
                names[t.Id.ToString("D")] = t.Name ?? t.Code;
            }
        }

        if (siteIds.Length > 0)
        {
            foreach (SiteSummary site in await directory.GetSitesAsync(siteIds, ct).ConfigureAwait(false))
            {
                names[site.SiteId.ToString("D")] = site.Name;
            }
        }

        if (names.Count == 0)
        {
            return changes;
        }

        string Name(string v) => names.TryGetValue(v, out string? n) ? n : v;
        return [.. changes.Select(c => c.IsList
            ? c with { Before = [.. c.Before.Select(Name).Order(StringComparer.CurrentCulture)], After = [.. c.After.Select(Name).Order(StringComparer.CurrentCulture)] }
            : c)];
    }

    /// <summary>Stores the listing's current form as a revision, in the caller's transaction, after its changes are saved.</summary>
    private async Task RecordRevisionAsync(ListingRecord listing, string kind, UserId actor, CancellationToken ct)
    {
        if (await ReadFormAsync(listing.Id, ct).ConfigureAwait(false) is not { } current)
        {
            return;
        }

        db.ListingRevisions.Add(new ListingRevisionRecord
        {
            Id = UuidV7.NewId(),
            TenantId = listing.TenantId,
            ListingId = listing.Id,
            Kind = kind,
            Status = listing.Status,
            Snapshot = ListingRevisions.Serialise(current.Form),
            CreatedAt = clock.UtcNow,
            CreatedBy = actor.Value,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ListingRow>> ListForOrganisationAsync(OrganisationId organisation, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ListingRow>>(async ct =>
        {
            List<ListingRecord> rows = await db.Listings.AsNoTracking()
                .Where(l => l.OrgId == organisation.Value)
                .OrderBy(l => l.Title).ThenBy(l => l.Slug)
                .ToListAsync(ct).ConfigureAwait(false);
            Guid[] ids = [.. rows.Select(l => l.Id)];
            Dictionary<Guid, PendingEdit> edits = (await db.ListingPendingEdits.AsNoTracking()
                    .Where(e => ids.Contains(e.ListingId))
                    .ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(e => e.ListingId, View);
            return [.. rows.Select(l => ToRow(l) with { Edit = edits.GetValueOrDefault(l.Id) })];
        }, cancellationToken);

    /// <summary>
    /// Listings submitted for review, oldest first; published listings whose schedule changed
    /// since an organiser last looked (D15), most recently changed first; and published listings
    /// whose edit waits for approval (D101), oldest first. A listing with an edit waiting is in
    /// that lane only: approving it marks the whole listing reviewed.
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

            string waiting = ListingPendingEditRecord.Waiting;
            var edits = await (from e in db.ListingPendingEdits.AsNoTracking()
                               join l in db.Listings.AsNoTracking() on e.ListingId equals l.Id
                               where e.State == waiting && l.Status == published
                               orderby e.SavedAt, l.Id
                               select new { Listing = l, Edit = e })
                .Take(200)
                .ToListAsync(ct).ConfigureAwait(false);

            List<ListingRecord> changed = await db.Listings.AsNoTracking()
                .Where(l => l.Status == published && (l.ReviewedAt == null || l.UpdatedAt > l.ReviewedAt)
                            && !db.ListingPendingEdits.Any(e => e.ListingId == l.Id && e.State == waiting))
                .OrderByDescending(l => l.UpdatedAt)
                .Take(200)
                .ToListAsync(ct).ConfigureAwait(false);

            return new ListingsForReview(
                [.. awaiting.Select(ToRow)],
                [.. changed.Select(ToRow)],
                [.. edits.Select(x => new PendingEditRow(ToRow(x.Listing) with { Edit = View(x.Edit) }, x.Edit.SavedAt))]);
        }, cancellationToken);

    /// <summary>The next sessions a form's schedule produces, or null when the schedule is not valid yet.</summary>
    public IReadOnlyList<Occurrence>? PreviewSessions(ListingForm form, int count)
    {
        (ParsedListing? parsed, _) = ListingFormParser.Parse(form);
        return parsed is null
            ? null
            : ListingFormParser.Preview(parsed, Zone, clock.TodayIn(TimeZoneInfo.FindSystemTimeZoneById(Zone)), count);
    }

    private async Task<Dictionary<string, Phrase[]>> CheckReferencesAsync(OrganisationId organisation, ParsedListing parsed, CancellationToken ct)
    {
        Dictionary<string, Phrase[]> errors = new(StringComparer.Ordinal);

        if (parsed.SiteIds.Count > 0)
        {
            IReadOnlyList<SiteSummary> sites = await directory.GetSitesAsync(parsed.SiteIds, ct).ConfigureAwait(false);
            if (sites.Count != parsed.SiteIds.Count || sites.Any(s => s.OrganisationId != organisation || !s.IsActive))
            {
                errors[nameof(ListingForm.SiteIds)] = [Phrase.Of("editor.error.sites_active")];
            }
        }

        Guid[] termIds = [.. parsed.TermIds, .. parsed.AccommodationIds];
        IReadOnlyList<TermSummary> terms = await taxonomy.GetTermsAsync(termIds, "en", ct).ConfigureAwait(false);
        Dictionary<Guid, TermSummary> byId = terms.ToDictionary(t => t.Id);

        if (parsed.TermIds.Any(id => !byId.TryGetValue(id, out TermSummary? t) || t.Vocabulary is not ("category" or "interest")))
        {
            errors[nameof(ListingForm.TermIds)] = [Phrase.Of("editor.error.terms_from_list")];
        }
        else if (!parsed.TermIds.Any(id => byId[id].Vocabulary == "category"))
        {
            errors[nameof(ListingForm.TermIds)] = [Phrase.Of("editor.error.categories")];
        }

        if (parsed.AccommodationIds.Any(id => !byId.TryGetValue(id, out TermSummary? t) || t.Vocabulary != "accommodation"))
        {
            errors[nameof(ListingForm.AccommodationIds)] = [Phrase.Of("editor.error.accommodations_from_list")];
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

        // An event stops being listed the day after it ends, or sooner if the provider says so.
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        listing.PublishAt = p.ShowFrom is { } show ? Local(zone, show, TimeOnly.MinValue) : null;
        listing.ExpiresAt = LastShown(p) is { } last ? Local(zone, last.AddDays(1), TimeOnly.MinValue) : null;
    }

    /// <summary>The last day a listing is in search: the take-down day, or an event's last day, whichever is sooner.</summary>
    private static DateOnly? LastShown(ParsedListing p)
    {
        DateOnly? eventEnd = p.Kind == ListingKind.Event ? p.EndsOn : null;
        return (p.TakeDownAfter, eventEnd) switch
        {
            ({ } t, { } e) => t < e ? t : e,
            ({ } t, null) => t,
            (null, { } e) => e,
            _ => null,
        };
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
        session.Exdates = [.. p.SkipDates];
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

    /// <summary>The same wall-clock time, <paramref name="days"/> later, so 23:59 stays 23:59 across a clock change.</summary>
    private static DateTimeOffset Shift(DateTimeOffset instant, int days, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(instant, zone).DateTime.AddDays(days);
        return OccurrenceExpander.ToUtc(local, zone);
    }

    /// <summary>
    /// A weekly rule's first session must fall on one of its days, or RFC 5545 makes the start
    /// an extra session. A shift of other than whole weeks can break that, so the clone's start
    /// moves forward to the first matching day, as the editor's does.
    /// </summary>
    private static DateOnly AlignedStart(string? rrule, DateOnly startsOn)
    {
        List<string> days = rrule is null ? [] : DaysOf(rrule);
        return days.Count == 0 ? startsOn : days.Select(d => ListingFormParser.NextOrSame(startsOn, d)).Min();
    }

    private static string Day(DateTimeOffset local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The take-down day as the provider typed it. An event's own end-of-run expiry is not a
    /// take-down the provider chose, so it shows as empty.
    /// </summary>
    private static string TakeDownText(ListingRecord l, ListingSessionRecord? session, TimeZoneInfo zone)
    {
        if (l.ExpiresAt is not { } expires)
        {
            return string.Empty;
        }

        DateOnly last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(expires, zone).DateTime).AddDays(-1);
        bool eventEnd = l.Kind == ListingKind.Event.ToDbValue() && session?.EndsOn == last;
        return eventEnd ? string.Empty : last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string IntervalOf(string rrule)
    {
        int at = rrule.IndexOf("INTERVAL=", StringComparison.Ordinal);
        return at < 0 ? "1" : rrule[(at + 9)..].Split(';')[0];
    }

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

/// <summary>
/// What an applied import did. Skipped rows are named by their external id. <c>Held</c> counts the
/// updates to published listings whose wording waits for an organiser (D101); they are among the
/// updated.
/// </summary>
public sealed record ImportOutcome(int Created, int Updated, IReadOnlyList<string> Skipped, int Held = 0);
