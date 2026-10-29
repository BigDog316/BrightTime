using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Decisions;
using BrightTime.Modules.Enrollment.Eligibility;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Modules.Enrollment.Forms;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Applications;

/// <summary>What the family says when submitting: what the organisation may see of the applicant, and until when.</summary>
public sealed record SubmitInput(IReadOnlyList<string> ShareFields, DateTimeOffset? ShareExpiresAt);

[YouthData]
public sealed record ApplicationSummary(Guid Id, Guid ListingId, string ListingTitle, Guid MemberId, string MemberFirstName, string Status, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt);

[YouthData]
public sealed record ApplicationView(
    Guid Id,
    Guid ListingId,
    string ListingTitle,
    Guid? OrganisationId,
    Guid MemberId,
    string MemberFirstName,
    string Status,
    Guid? FormId,
    IReadOnlyDictionary<string, JsonElement> Answers,
    EligibilityResult Eligibility,
    DateTimeOffset StartedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? WithdrawnAt);

/// <summary>
/// A family's applications (M3a, D46): one member to one listing, drafted against the listing's
/// form, judged by its eligibility rules, and submitted once.
/// </summary>
/// <remarks>
/// <para>
/// Only the profile's owner reads or changes them. Creating one needs an
/// <c>Idempotency-Key</c>, so an app retrying on a bad connection gets the same draft back
/// rather than a second one. Submitting twice is the same submission.
/// </para>
/// <para>
/// Submitting shares the applicant's profile with the organisation (D44), with the fields the
/// family chose, which must include the applicant's name: an organisation cannot consider an
/// application from no one. A hard eligibility rule the applicant fails stops the submission;
/// one that can't be decided yet (a grade not given) does not, and the provider decides.
/// </para>
/// <para>
/// Every waiver the listing requires must be signed first (D47).
/// </para>
/// <para>
/// Sensitive answers (D45) are sealed under the tenant's data key in the snapshot, and their
/// reporting rows record only that they were answered.
/// </para>
/// </remarks>
public sealed partial class Applications(
    EnrollmentDbContext db,
    IFieldCipher cipher,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IListingDetails listings,
    EligibilityRules eligibility,
    ProfileShares shares,
    Waivers.Waivers waivers,
    SealedAnswers sealedAnswers,
    SeatLedger seats,
    IAchievementQueries achievements)
{
    public const string Read = "enrollment.application.read";
    public const string Changed = "enrollment.application.changed";

    /// <summary>A new draft, or the one this key already made. The flag says which.</summary>
    public Task<Result<(ApplicationView View, bool Created)>> CreateAsync(UserId owner, Guid listingId, Guid memberId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (idempotencyKey is null || !Key().IsMatch(idempotencyKey))
            {
                return Fail<(ApplicationView, bool)>(Error.Validation("application.idempotency_key", "An Idempotency-Key header of 8 to 100 letters, digits, '-' or '_'."));
            }

            if (await db.Applications.FirstOrDefaultAsync(a => a.TenantId == tenant.TenantId.Value && a.IdempotencyKey == idempotencyKey, ct).ConfigureAwait(false) is { } again)
            {
                if (await OwnAsync(owner, again.Id, ct).ConfigureAwait(false) is null)
                {
                    return Fail<(ApplicationView, bool)>(Error.Conflict("application.idempotency_key", "That Idempotency-Key was used for something else."));
                }

                ApplicationView same = await ViewAsync(again, ct).ConfigureAwait(false);
                await AuditAsync(Read, again.Id, null, ct).ConfigureAwait(false);
                return Result<(ApplicationView, bool)>.Success((same, false));
            }

            if (await db.FamilyProfiles.FirstOrDefaultAsync(p => p.OwnerUserId == owner.Value, ct).ConfigureAwait(false) is not { } profile)
            {
                return Fail<(ApplicationView, bool)>(Error.Precondition("family.no_profile", "Save the household's details first."));
            }

            FamilyMemberRecord? member = await db.FamilyMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.FamilyProfileId == profile.Id, ct).ConfigureAwait(false);
            if (member is null || !FamilyMemberRoles.IsYoungPerson(member.MemberRole))
            {
                return Fail<(ApplicationView, bool)>(Error.Validation("application.member", "A young person in your household."));
            }

            ListingDetail? listing = await listings.GetForReviewAsync(tenant.TenantId, listingId, tenant.Locale.Code, ct).ConfigureAwait(false);
            if (listing is null || await listings.GetPublishedAsync(tenant.TenantId, listing.Slug, tenant.Locale.Code, ct).ConfigureAwait(false) is null)
            {
                return Fail<(ApplicationView, bool)>(Error.NotFound("application.listing", "No published activity has that id."));
            }

            if (listing.RegistrationMode is not (RegistrationMode.Builtin or RegistrationMode.WaitlistOnly))
            {
                return Fail<(ApplicationView, bool)>(Error.Validation("application.registration", "This activity doesn't take registrations here."));
            }

            if (await db.Applications.AnyAsync(a => a.ListingId == listingId && a.FamilyMemberId == memberId && Live.Contains(a.Status), ct).ConfigureAwait(false))
            {
                return Fail<(ApplicationView, bool)>(Error.Conflict("application.exists", "This young person already has an application for this activity."));
            }

            Guid? form = await (from l in db.ListingForms
                                join f in db.FormDefinitions on l.FormDefinitionId equals f.Id
                                where l.ListingId == listingId && f.Status == FormDefinitions.Published
                                select (Guid?)f.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            ApplicationRecord application = new()
            {
                Id = UuidV7.NewId(),
                TenantId = profile.TenantId,
                ListingId = listingId,
                FamilyProfileId = profile.Id,
                FamilyMemberId = member.Id,
                FormDefinitionId = form,
                Status = ApplicationStatuses.Draft,
                AnswersSnapshot = "{}",
                EligibilityResult = "{}",
                StartedAt = now,
                IdempotencyKey = idempotencyKey,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Applications.Add(application);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, application.Id, "created", ct).ConfigureAwait(false);
            return Result<(ApplicationView, bool)>.Success((await ViewAsync(application, ct).ConfigureAwait(false), true));
        }, cancellationToken);

    public Task<IReadOnlyList<ApplicationSummary>> ListAsync(UserId owner, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ApplicationSummary>>(async ct =>
        {
            List<(ApplicationRecord Application, FamilyMemberRecord Member)> mine = [.. (await (from a in db.Applications.AsNoTracking()
                                                                                              join p in db.FamilyProfiles on a.FamilyProfileId equals p.Id
                                                                                              join m in db.FamilyMembers on a.FamilyMemberId equals m.Id
                                                                                              where p.OwnerUserId == owner.Value
                                                                                              orderby a.StartedAt descending
                                                                                              select new { a, m }).Take(200).ToListAsync(ct).ConfigureAwait(false))
                .Select(x => (x.a, x.m))];
            List<ApplicationSummary> summaries = [];
            foreach ((ApplicationRecord a, FamilyMemberRecord m) in mine)
            {
                summaries.Add(new ApplicationSummary(a.Id, a.ListingId, await TitleAsync(a.ListingId, ct).ConfigureAwait(false), m.Id,
                    await cipher.DecryptAsync(tenant.TenantId, m.FirstNameEnc, ct).ConfigureAwait(false) ?? "", a.Status, a.StartedAt, a.SubmittedAt));
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "application", SubjectIds = [.. mine.Select(x => x.Application.Id)] }, ct).ConfigureAwait(false);
            return summaries;
        }, cancellationToken);

    public Task<Result<ApplicationView>> GetAsync(UserId owner, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Fail<ApplicationView>(NotFound());
            }

            ApplicationView view = await ViewAsync(application, ct).ConfigureAwait(false);
            await AuditAsync(Read, application.Id, null, ct).ConfigureAwait(false);
            return Result<ApplicationView>.Success(view);
        }, cancellationToken);

    /// <summary>
    /// The form version an application answers (D45, D72), worded for the family. The listing's
    /// own form may have moved on to a later version since the draft began; this one doesn't.
    /// </summary>
    public Task<Result<PublicForm>> QuestionsAsync(UserId owner, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Fail<PublicForm>(NotFound());
            }

            if (application.FormDefinitionId is not { } formId)
            {
                return Fail<PublicForm>(Error.NotFound("application.no_form", "This activity asks no questions."));
            }

            FormDefinitionRecord form = await db.FormDefinitions.AsNoTracking().FirstAsync(f => f.Id == formId, ct).ConfigureAwait(false);
            await AuditAsync(Read, application.Id, null, ct).ConfigureAwait(false);
            return Result<PublicForm>.Success(FormDefinitions.Public(form, tenant.Locale.Code));
        }, cancellationToken);

    /// <summary>Saves some of a draft's answers; a null answer clears that question. Nothing is required until submitting.</summary>
    public Task<Result<ApplicationView>> SaveAnswersAsync(UserId owner, Guid id, IReadOnlyDictionary<string, JsonElement> answers, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(answers);
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Fail<ApplicationView>(NotFound());
            }

            if (application.Status != ApplicationStatuses.Draft)
            {
                return Fail<ApplicationView>(Error.Conflict("application.not_draft", "A submitted application can't be changed."));
            }

            if (await FormAsync(application, ct).ConfigureAwait(false) is not { } form)
            {
                return Fail<ApplicationView>(Error.Validation("application.no_form", "This activity asks no questions."));
            }

            if (FormAnswers.Check(form, answers) is { } invalid)
            {
                return Fail<ApplicationView>(invalid);
            }

            JsonObject snapshot = JsonNode.Parse(application.AnswersSnapshot)!.AsObject();
            Dictionary<string, FormQuestion> questions = form.Questions.ToDictionary(q => q.Key, StringComparer.Ordinal);
            foreach ((string key, JsonElement value) in answers)
            {
                snapshot.Remove(key);
                if (value.ValueKind != JsonValueKind.Null)
                {
                    snapshot[key] = questions[key].Sensitive ? await SealAsync(value, ct).ConfigureAwait(false) : JsonNode.Parse(value.GetRawText());
                }
            }

            application.AnswersSnapshot = snapshot.ToJsonString();
            application.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, application.Id, "answers", ct).ConfigureAwait(false);
            return Result<ApplicationView>.Success(await ViewAsync(application, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// Submits a draft: complete, not failing a hard rule, and shared with the organisation as the
    /// family chose. Submitting an application already submitted returns it unchanged.
    /// </summary>
    public Task<Result<ApplicationView>> SubmitAsync(UserId owner, Guid id, SubmitInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Fail<ApplicationView>(NotFound());
            }

            if (application.Status != ApplicationStatuses.Draft)
            {
                ApplicationView already = await ViewAsync(application, ct).ConfigureAwait(false);
                await AuditAsync(Read, application.Id, null, ct).ConfigureAwait(false);
                return application.SubmittedAt is not null
                    ? Result<ApplicationView>.Success(already)
                    : Fail<ApplicationView>(Error.Conflict("application.not_draft", "This application can no longer be submitted."));
            }

            if (!input.ShareFields.Contains(ProfileShareFields.MemberNames))
            {
                return Fail<ApplicationView>(Error.Validation("application.share", "Share at least the young person's name with the organisation."));
            }

            FormSchema? form = await FormAsync(application, ct).ConfigureAwait(false);
            Dictionary<string, JsonElement> answers = await AnswersAsync(application, ct).ConfigureAwait(false);
            if (form is not null && FormAnswers.CheckComplete(form, answers) is { } incomplete)
            {
                return Fail<ApplicationView>(incomplete);
            }

            // Every waiver the listing requires, signed for this young person, in the text it has now (D47).
            if (await waivers.UnsignedAsync(application.ListingId, application.FamilyMemberId, ct).ConfigureAwait(false) is { Count: > 0 } unsigned)
            {
                return Fail<ApplicationView>(Error.Validation("application.waivers_unsigned", "Sign every required waiver first.",
                    arguments: new Dictionary<string, object>(StringComparer.Ordinal) { ["waivers"] = string.Join(',', unsigned) }));
            }

            FamilyMemberRecord member = await db.FamilyMembers.FirstAsync(m => m.Id == application.FamilyMemberId, ct).ConfigureAwait(false);
            EligibilityResult judged = await eligibility.EvaluateAsync(application.ListingId, await ApplicantAsync(member, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (judged.IsEligible == false)
            {
                return Fail<ApplicationView>(Error.Conflict("application.ineligible", "This young person doesn't meet the activity's requirements.",
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["rules"] = string.Join(',', judged.Rules.Where(r => r.IsHard && r.Met == false).Select(r => r.Kind)) }));
            }

            // A family holds one live share per organisation and listing, so a second child's
            // submission joins it rather than replacing it: replacing would cut the organisation
            // off from the first child (D48). The fields are the union; the family sees them, and
            // can narrow or end the share, under /family-profile/shares.
            ListingDetail listing = (await listings.GetForReviewAsync(tenant.TenantId, application.ListingId, Locale.Default.Code, ct).ConfigureAwait(false))!;
            Guid org = listing.OrganisationId!.Value.Value;
            DateTimeOffset asOf = clock.StoredNow();
            ProfileShareRecord? existing = await db.ProfileShares.AsNoTracking()
                .FirstOrDefaultAsync(s => s.FamilyProfileId == application.FamilyProfileId && s.OrgId == org && s.ListingId == application.ListingId
                                          && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > asOf), ct).ConfigureAwait(false);
            Result<ShareView> shared = await shares.GrantAsync(owner, new ShareInput(
                org,
                application.ListingId,
                [.. (existing?.MemberIds ?? []).Append(member.Id).Distinct()],
                [.. (existing?.SharedFields ?? []).Concat(input.ShareFields).Distinct(StringComparer.Ordinal)],
                input.ShareExpiresAt ?? existing?.ExpiresAt), ct).ConfigureAwait(false);
            if (!shared.IsSuccess)
            {
                return Fail<ApplicationView>(shared.Error);
            }

            if (form is not null)
            {
                Dictionary<string, FormQuestion> questions = form.Questions.ToDictionary(q => q.Key, StringComparer.Ordinal);
                db.ApplicationAnswers.AddRange(answers.Select(a => FormAnswers.Row(application.Id, questions[a.Key], a.Value)));
            }

            DateTimeOffset now = clock.StoredNow();
            application.Status = ApplicationStatuses.Submitted;
            application.SubmittedAt = now;
            application.SubmittedByUserId = owner.Value;
            application.EligibilityResult = EligibilityRules.ToJson(judged);
            application.IsEligible = judged.IsEligible;
            application.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, application.Id, "submitted", ct).ConfigureAwait(false);
            return Result<ApplicationView>.Success(await ViewAsync(application, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// Withdraws a submitted application, or throws away a draft (which was never sent). The share
    /// the submission made stays until the family revokes it.
    /// </summary>
    public Task<Result> WithdrawAsync(UserId owner, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Result.Failure(NotFound());
            }

            if (application.Status == ApplicationStatuses.Draft)
            {
                db.Applications.Remove(application);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await AuditAsync(Changed, application.Id, "draft_discarded", ct).ConfigureAwait(false);
                return Result.Success();
            }

            if (!ApplicationStatuses.Withdrawable.Contains(application.Status))
            {
                return Result.Failure(Error.Conflict("application.not_withdrawable", "This application has already ended."));
            }

            // Any seat or waitlist place it holds goes back first (D48).
            await seats.ReleaseAsync(application, EnrollmentStatuses.Withdrawn, "family_withdrew", ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            application.Status = ApplicationStatuses.Withdrawn;
            application.WithdrawnAt = now;
            application.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, application.Id, "withdrawn", ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Takes a seat offered from the waitlist: the young person is enrolled (D48).</summary>
    public Task<Result<ApplicationView>> TakeOfferAsync(UserId owner, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Fail<ApplicationView>(NotFound());
            }

            if (application.Status != ApplicationStatuses.Offered)
            {
                return Fail<ApplicationView>(Error.Conflict("application.no_offer", "There's no seat on offer for this application."));
            }

            await seats.TakeOfferAsync(application, ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            application.Status = ApplicationStatuses.Accepted;
            application.DecidedAt ??= now;
            application.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, application.Id, "offer_taken", ct).ConfigureAwait(false);
            return Result<ApplicationView>.Success(await ViewAsync(application, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Turns down a seat offered from the waitlist; the seat goes back for the next family.</summary>
    public Task<Result> DeclineOfferAsync(UserId owner, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(owner, id, ct).ConfigureAwait(false) is not { } application)
            {
                return Result.Failure(NotFound());
            }

            if (application.Status != ApplicationStatuses.Offered)
            {
                return Result.Failure(Error.Conflict("application.no_offer", "There's no seat on offer for this application."));
            }

            await seats.ReleaseAsync(application, EnrollmentStatuses.Withdrawn, "offer_declined", ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            application.Status = ApplicationStatuses.Withdrawn;
            application.WithdrawnAt = now;
            application.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, application.Id, "offer_declined", ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    private static readonly string[] Live =
        [ApplicationStatuses.Draft, ApplicationStatuses.Submitted, ApplicationStatuses.UnderReview, ApplicationStatuses.Waitlisted, ApplicationStatuses.Offered, ApplicationStatuses.Accepted];

    private async Task<ApplicationRecord?> OwnAsync(UserId owner, Guid id, CancellationToken ct) =>
        await (from a in db.Applications
               join p in db.FamilyProfiles on a.FamilyProfileId equals p.Id
               where a.Id == id && p.OwnerUserId == owner.Value
               select a).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<FormSchema?> FormAsync(ApplicationRecord application, CancellationToken ct) =>
        application.FormDefinitionId is { } formId
            ? FormSchema.FromJson(await db.FormDefinitions.Where(f => f.Id == formId).Select(f => f.SchemaJson).FirstAsync(ct).ConfigureAwait(false))
            : null;

    private async Task<ApplicationView> ViewAsync(ApplicationRecord application, CancellationToken ct)
    {
        FamilyMemberRecord member = await db.FamilyMembers.AsNoTracking().FirstAsync(m => m.Id == application.FamilyMemberId, ct).ConfigureAwait(false);
        ListingDetail? listing = await listings.GetForReviewAsync(tenant.TenantId, application.ListingId, tenant.Locale.Code, ct).ConfigureAwait(false);
        EligibilityResult judged = application.Status == ApplicationStatuses.Draft
            ? await eligibility.EvaluateAsync(application.ListingId, await ApplicantAsync(member, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
            : JsonSerializer.Deserialize<EligibilityResult>(application.EligibilityResult, JsonSerializerOptions.Web) ?? new EligibilityResult(null, []);
        return new ApplicationView(
            application.Id,
            application.ListingId,
            listing?.Title ?? "",
            listing?.OrganisationId?.Value,
            member.Id,
            await cipher.DecryptAsync(tenant.TenantId, member.FirstNameEnc, ct).ConfigureAwait(false) ?? "",
            application.Status,
            application.FormDefinitionId,
            await AnswersAsync(application, ct).ConfigureAwait(false),
            judged,
            application.StartedAt,
            application.SubmittedAt,
            application.WithdrawnAt);
    }

    private async Task<Applicant> ApplicantAsync(FamilyMemberRecord member, CancellationToken ct)
    {
        string? dob = await cipher.DecryptAsync(tenant.TenantId, member.DateOfBirthEnc, ct).ConfigureAwait(false);
        Achievements achieved = member.UserId is { } account ? await achievements.OfAsync(account, ct).ConfigureAwait(false) : Achievements.None;
        return new Applicant(dob is null ? null : DateOnly.ParseExact(dob, "yyyy-MM-dd", CultureInfo.InvariantCulture), member.Grade, member.NeighbourhoodId, achieved);
    }

    private Task<Dictionary<string, JsonElement>> AnswersAsync(ApplicationRecord application, CancellationToken ct) =>
        sealedAnswers.ReadAsync(application.AnswersSnapshot, ct);

    private Task<JsonNode> SealAsync(JsonElement value, CancellationToken ct) => sealedAnswers.SealAsync(value, ct);

    private async Task<string> TitleAsync(Guid listingId, CancellationToken ct) =>
        (await listings.GetForReviewAsync(tenant.TenantId, listingId, tenant.Locale.Code, ct).ConfigureAwait(false))?.Title ?? "";

    private Task AuditAsync(string action, Guid application, string? change, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "application",
            SubjectIds = [application],
            Detail = change is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.Ordinal) { ["change"] = change },
        }, ct);

    private static Result<T> Fail<T>(Error error) => Result<T>.Failure(error);

    private static Error NotFound() => Error.NotFound("application.not_found", "You have no application with that id.");

    [GeneratedRegex("^[A-Za-z0-9_-]{8,100}$")]
    private static partial Regex Key();
}
