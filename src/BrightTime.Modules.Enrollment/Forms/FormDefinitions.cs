using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Forms;

public sealed record FormSummary(Guid Id, string Name, int Version, string Status, int QuestionCount, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

public sealed record FormView(
    Guid Id,
    string Name,
    int Version,
    string Status,
    IReadOnlyList<FormQuestion> Questions,
    string Hash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<Guid> Listings);

public sealed record PublicChoice(string Key, string Label);

public sealed record PublicQuestion(
    string Key,
    string Kind,
    string Label,
    string? Help,
    bool Required,
    bool Sensitive,
    IReadOnlyList<PublicChoice>? Choices,
    int? MaxLength,
    decimal? Min,
    decimal? Max);

/// <summary>A listing's form as a family is shown it, in their language where the provider wrote one.</summary>
public sealed record PublicForm(Guid FormId, int Version, string Hash, IReadOnlyList<PublicQuestion> Questions);

/// <summary>
/// Registration forms (M3a, D45): written by an organisation, versioned, and attached to its
/// listings.
/// </summary>
/// <remarks>
/// <para>
/// A form is a draft until published, and a published version never changes: an application
/// records which version it answered (<c>application.form_definition_id</c>), so a dispute about
/// what was asked has one answer. Changing a published form makes the next version, as a draft.
/// </para>
/// <para>
/// The caller has checked the actor may manage the organisation's forms; every method here is
/// scoped to that organisation and finds nothing of anyone else's.
/// </para>
/// </remarks>
public sealed class FormDefinitions(EnrollmentDbContext db, ITenantContext tenant, IClock clock, IListingDetails listings)
{
    public const int MaxNameLength = 120;

    public const string Draft = "draft";
    public const string Published = "published";
    public const string Retired = "retired";

    public Task<IReadOnlyList<FormSummary>> ListAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<FormSummary>>(async ct =>
        {
            List<FormDefinitionRecord> forms = await db.FormDefinitions.AsNoTracking()
                .Where(f => f.OrgId == org.Value).OrderBy(f => f.Name).ThenByDescending(f => f.Version)
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. forms.Select(f => new FormSummary(f.Id, f.Name, f.Version, f.Status, FormSchema.FromJson(f.SchemaJson).Questions.Count, f.CreatedAt, f.PublishedAt))];
        }, cancellationToken);

    public Task<Result<FormView>> GetAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await OwnAsync(org, id, ct).ConfigureAwait(false) is { } form
                ? Result<FormView>.Success(await ViewAsync(form, ct).ConfigureAwait(false))
                : Result<FormView>.Failure(NotFound()), cancellationToken);

    /// <summary>A new form, as version 1 of a draft. The name stays with the form through its versions.</summary>
    public Task<Result<FormView>> CreateAsync(OrganisationId org, UserId author, string name, FormSchema schema, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(schema);
            string? trimmed = name?.Trim();
            if (trimmed is not { Length: > 0 and <= MaxNameLength } || trimmed.Any(char.IsControl))
            {
                return Result<FormView>.Failure(Error.Validation("form.name", $"A name of 1 to {MaxNameLength} characters."));
            }

            if (schema.Check() is { } invalid)
            {
                return Result<FormView>.Failure(invalid);
            }

            if (await db.FormDefinitions.AnyAsync(f => f.OrgId == org.Value && f.Name == trimmed, ct).ConfigureAwait(false))
            {
                return Result<FormView>.Failure(Error.Conflict("form.name_taken", "Your organisation already has a form with that name."));
            }

            FormDefinitionRecord form = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                Name = trimmed,
                Version = 1,
                Status = Draft,
                SchemaJson = schema.ToCanonicalJson(),
                SchemaHash = schema.Hash(),
                CreatedBy = author.Value,
                CreatedAt = clock.StoredNow(),
            };
            db.FormDefinitions.Add(form);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<FormView>.Success(await ViewAsync(form, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Replaces a draft's questions. A published version is fixed; revise it instead.</summary>
    public Task<Result<FormView>> UpdateAsync(OrganisationId org, Guid id, FormSchema schema, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(schema);
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } form)
            {
                return Result<FormView>.Failure(NotFound());
            }

            if (form.Status != Draft)
            {
                return Result<FormView>.Failure(Error.Conflict("form.not_draft", "A published form never changes. Make a new version to change it."));
            }

            if (schema.Check() is { } invalid)
            {
                return Result<FormView>.Failure(invalid);
            }

            form.SchemaJson = schema.ToCanonicalJson();
            form.SchemaHash = schema.Hash();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<FormView>.Success(await ViewAsync(form, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Result<FormView>> PublishAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } form)
            {
                return Result<FormView>.Failure(NotFound());
            }

            if (form.Status != Draft)
            {
                return Result<FormView>.Failure(Error.Conflict("form.not_draft", "Only a draft can be published."));
            }

            form.Status = Published;
            form.PublishedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<FormView>.Success(await ViewAsync(form, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>The next version of a published or retired form, as a draft holding its questions.</summary>
    public Task<Result<FormView>> ReviseAsync(OrganisationId org, Guid id, UserId author, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } source)
            {
                return Result<FormView>.Failure(NotFound());
            }

            if (source.Status == Draft)
            {
                return Result<FormView>.Failure(Error.Conflict("form.not_published", "A draft can be changed as it is."));
            }

            List<FormDefinitionRecord> family = await db.FormDefinitions
                .Where(f => f.OrgId == org.Value && f.Name == source.Name).ToListAsync(ct).ConfigureAwait(false);
            if (family.Any(f => f.Status == Draft))
            {
                return Result<FormView>.Failure(Error.Conflict("form.draft_exists", "This form already has a draft of its next version."));
            }

            FormDefinitionRecord next = new()
            {
                Id = UuidV7.NewId(),
                TenantId = source.TenantId,
                OrgId = source.OrgId,
                Name = source.Name,
                Version = family.Max(f => f.Version) + 1,
                Status = Draft,
                SchemaJson = source.SchemaJson,
                SchemaHash = source.SchemaHash,
                CreatedBy = author.Value,
                CreatedAt = clock.StoredNow(),
            };
            db.FormDefinitions.Add(next);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<FormView>.Success(await ViewAsync(next, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Stops a published version being attached anywhere new. Refused while a listing still asks for it.</summary>
    public Task<Result<FormView>> RetireAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } form)
            {
                return Result<FormView>.Failure(NotFound());
            }

            if (form.Status != Published)
            {
                return Result<FormView>.Failure(Error.Conflict("form.not_published", "Only a published form can be retired."));
            }

            if (await db.ListingForms.AnyAsync(l => l.FormDefinitionId == form.Id, ct).ConfigureAwait(false))
            {
                return Result<FormView>.Failure(Error.Conflict("form.in_use", "A listing still asks families to fill this in. Give it another form first."));
            }

            form.Status = Retired;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<FormView>.Success(await ViewAsync(form, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Makes a published form the one a listing's registration asks for, replacing any other.</summary>
    public Task<Result> AttachAsync(OrganisationId org, Guid listingId, Guid formId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            if (await OwnAsync(org, formId, ct).ConfigureAwait(false) is not { } form)
            {
                return Result.Failure(NotFound());
            }

            if (form.Status != Published)
            {
                return Result.Failure(Error.Conflict("form.not_published", "Publish the form before a listing uses it."));
            }

            ListingFormRecord? current = await db.ListingForms.FirstOrDefaultAsync(l => l.ListingId == listingId, ct).ConfigureAwait(false);
            if (current is null)
            {
                db.ListingForms.Add(new ListingFormRecord { ListingId = listingId, FormDefinitionId = form.Id });
            }
            else
            {
                current.FormDefinitionId = form.Id;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<Result> DetachAsync(OrganisationId org, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            await db.ListingForms.Where(l => l.ListingId == listingId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The form a published listing asks for, in <paramref name="locale"/>; null when it asks for none.</summary>
    public Task<PublicForm?> ForListingAsync(string slug, string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<PublicForm?>(async ct =>
        {
            if (await listings.GetPublishedAsync(tenant.TenantId, slug, locale, ct).ConfigureAwait(false) is not { } listing)
            {
                return null;
            }

            Guid listingId = listing.Id.Value;
            FormDefinitionRecord? form = await (from l in db.ListingForms
                                                join f in db.FormDefinitions on l.FormDefinitionId equals f.Id
                                                where l.ListingId == listingId && f.Status == Published
                                                select f).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (form is null)
            {
                return null;
            }

            return Public(form, locale);
        }, cancellationToken);

    /// <summary>A form version as a family is shown it, in their language where the provider wrote one.</summary>
    internal static PublicForm Public(FormDefinitionRecord form, string locale)
    {
        FormSchema schema = FormSchema.FromJson(form.SchemaJson);
        return new PublicForm(form.Id, form.Version, Convert.ToHexStringLower(form.SchemaHash), [.. schema.Questions.Select(q => new PublicQuestion(
            q.Key,
            q.Kind,
            FormSchema.In(q.Label, locale) ?? q.Key,
            FormSchema.In(q.Help, locale),
            q.Required,
            q.Sensitive,
            q.Choices?.Select(c => new PublicChoice(c.Key, FormSchema.In(c.Label, locale) ?? c.Key)).ToList(),
            q.MaxLength,
            q.Min,
            q.Max))]);
    }

    private Task<FormDefinitionRecord?> OwnAsync(OrganisationId org, Guid id, CancellationToken ct) =>
        db.FormDefinitions.FirstOrDefaultAsync(f => f.Id == id && f.OrgId == org.Value, ct);

    private async Task<bool> OwnsListingAsync(OrganisationId org, Guid listingId, CancellationToken ct) =>
        (await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org;

    private async Task<FormView> ViewAsync(FormDefinitionRecord form, CancellationToken ct)
    {
        List<Guid> used = await db.ListingForms.AsNoTracking().Where(l => l.FormDefinitionId == form.Id)
            .Select(l => l.ListingId).ToListAsync(ct).ConfigureAwait(false);
        return new FormView(form.Id, form.Name, form.Version, form.Status, FormSchema.FromJson(form.SchemaJson).Questions,
            Convert.ToHexStringLower(form.SchemaHash), form.CreatedAt, form.PublishedAt, used);
    }

    private static Error NotFound() => Error.NotFound("form.not_found", "Your organisation has no form with that id.");
}
