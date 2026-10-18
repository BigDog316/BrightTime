using System.Globalization;
using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Taxonomy;

/// <summary>A term in the organiser's list: its code, place, state and its name in each of the tenant's languages.</summary>
public sealed record TermRow(Guid Id, string Code, Guid? ParentId, short SortOrder, bool IsActive, IReadOnlyDictionary<string, string> Names)
{
    public string Name(string locale) =>
        Names.TryGetValue(locale, out string? n) ? n : Names.TryGetValue("en", out string? en) ? en : Code;
}

/// <summary>A term as the organiser's form posts it.</summary>
public sealed record TermForm
{
    public string Code { get; init; } = "";

    /// <summary>The parent category's id, for an interest.</summary>
    public string ParentId { get; init; } = "";

    public string SortOrder { get; init; } = "";

    /// <summary>Name per locale code.</summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
}

/// <summary>What a save did: the term's id, or the fields that were wrong.</summary>
public sealed record TermSaveOutcome(Guid? TermId, IReadOnlyDictionary<string, Phrase[]> Errors)
{
    public bool Saved => TermId is not null && Errors.Count == 0;
}

/// <summary>
/// The organiser's taxonomy: categories, interests (under a category), accommodations and
/// skills, named in each of the tenant's languages.
/// </summary>
/// <remarks>
/// <para>
/// A code never changes once made: it is in facet values (<c>cat:stem</c>), saved searches and
/// links. A term is retired rather than deleted, because listings and history still name it;
/// a retired term leaves the editors' pickers and the finder's filters but keeps its facet on
/// listings already tagged, until they are re-tagged.
/// </para>
/// <para>
/// English is the fallback every page uses when a translation is missing, so every term must
/// have an English name. A rename or a change of state queues <see cref="TermChangedJob"/>, so
/// listings tagged with the term are re-projected with its current name.
/// </para>
/// </remarks>
public sealed partial class TaxonomyEditor(PlatformDbContext db, ITenantContext tenant, IAuditWriter audit)
{
    public const int MaxName = 80;

    public const string Fallback = "en";

    /// <summary>The vocabularies the organiser manages here, in the order the page lists them.</summary>
    public static readonly IReadOnlyList<string> Vocabularies = ["category", "interest", "accommodation", "skill"];

    public static bool IsManaged(string vocabulary) => Vocabularies.Contains(vocabulary, StringComparer.Ordinal);

    /// <summary>The tenant's languages, default first, as the form shows a name field for each.</summary>
    public Task<IReadOnlyList<string>> LocalesAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<string>>(async ct =>
        {
            List<string> locales = await db.TenantLocales.AsNoTracking()
                .OrderByDescending(l => l.IsDefault).ThenBy(l => l.SortOrder).ThenBy(l => l.Locale)
                .Select(l => l.Locale)
                .ToListAsync(ct).ConfigureAwait(false);
            return locales.Contains(Fallback, StringComparer.Ordinal) ? locales : [Fallback, .. locales];
        }, cancellationToken);

    /// <summary>Every term of a vocabulary, active and retired, in display order.</summary>
    public Task<IReadOnlyList<TermRow>> ListAsync(string vocabulary, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<TermRow>>(async ct =>
        {
            List<TaxonomyTerm> terms = await db.TaxonomyTerms.AsNoTracking()
                .Where(t => t.Vocabulary == vocabulary)
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Code)
                .ToListAsync(ct).ConfigureAwait(false);
            Guid[] ids = [.. terms.Select(t => t.Id)];
            List<TaxonomyTermName> names = await db.TaxonomyTermNames.AsNoTracking()
                .Where(n => ids.Contains(n.TermId))
                .ToListAsync(ct).ConfigureAwait(false);
            ILookup<Guid, TaxonomyTermName> byTerm = names.ToLookup(n => n.TermId);
            return [.. terms.Select(t => Row(t, byTerm[t.Id]))];
        }, cancellationToken);

    public Task<TermRow?> GetAsync(Guid termId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            TaxonomyTerm? t = await db.TaxonomyTerms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == termId, ct).ConfigureAwait(false);
            if (t is null)
            {
                return null;
            }

            List<TaxonomyTermName> names = await db.TaxonomyTermNames.AsNoTracking().Where(n => n.TermId == termId).ToListAsync(ct).ConfigureAwait(false);
            return Row(t, names);
        }, cancellationToken);

    /// <summary>Creates a term (<paramref name="termId"/> null) or renames and moves one. The code of an existing term is not changed.</summary>
    public async Task<TermSaveOutcome> SaveAsync(string vocabulary, Guid? termId, TermForm form, UserId actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!IsManaged(vocabulary))
        {
            throw new ArgumentException("Not a vocabulary the organiser manages.", nameof(vocabulary));
        }

        IReadOnlyList<string> locales = await LocalesAsync(cancellationToken).ConfigureAwait(false);
        return await db.InTransactionAsync(async ct =>
        {
            Dictionary<string, Phrase[]> errors = new(StringComparer.Ordinal);
            TaxonomyTerm? term = null;
            if (termId is { } id)
            {
                term = await db.TaxonomyTerms.FirstOrDefaultAsync(t => t.Id == id && t.Vocabulary == vocabulary, ct).ConfigureAwait(false);
                if (term is null)
                {
                    return new TermSaveOutcome(null, new Dictionary<string, Phrase[]> { ["*"] = [Phrase.Of("taxonomy.error.not_found")] });
                }
            }

            string code = term?.Code ?? form.Code.Trim().ToLowerInvariant();
            if (term is null)
            {
                if (code.Length is < 2 or > 40 || !CodePattern().IsMatch(code))
                {
                    errors[nameof(TermForm.Code)] = [Phrase.Of("taxonomy.error.code")];
                }
                else if (await db.TaxonomyTerms.AnyAsync(t => t.Vocabulary == vocabulary && t.Code == code, ct).ConfigureAwait(false))
                {
                    errors[nameof(TermForm.Code)] = [Phrase.Of("taxonomy.error.code_taken", ("code", code))];
                }
            }

            Guid? parent = null;
            if (vocabulary == "interest")
            {
                if (!Guid.TryParse(form.ParentId, out Guid p)
                    || !await db.TaxonomyTerms.AnyAsync(t => t.Id == p && t.Vocabulary == "category" && t.IsActive, ct).ConfigureAwait(false))
                {
                    errors[nameof(TermForm.ParentId)] = [Phrase.Of("taxonomy.error.parent")];
                }
                else
                {
                    parent = p;
                }
            }

            short order = 0;
            if (string.IsNullOrWhiteSpace(form.SortOrder))
            {
                short last = await db.TaxonomyTerms.Where(t => t.Vocabulary == vocabulary).Select(t => (short?)t.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                order = term?.SortOrder ?? (short)Math.Min(short.MaxValue, last + 10);
            }
            else if (!short.TryParse(form.SortOrder.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out order))
            {
                errors[nameof(TermForm.SortOrder)] = [Phrase.Of("editor.error.whole_number")];
            }

            Dictionary<string, string> names = new(StringComparer.Ordinal);
            foreach (string locale in locales)
            {
                string name = form.Names.TryGetValue(locale, out string? n) ? n.Trim() : string.Empty;
                if (name.Length > MaxName)
                {
                    errors[NameField(locale)] = [Phrase.Of("editor.error.too_long", ("max", MaxName), ("length", name.Length))];
                }
                else if (name.Length > 0)
                {
                    names[locale] = name;
                }
            }

            if (!names.ContainsKey(Fallback) && !errors.ContainsKey(NameField(Fallback)))
            {
                errors[NameField(Fallback)] = [Phrase.Of("taxonomy.error.english_name")];
            }

            if (errors.Count > 0)
            {
                return new TermSaveOutcome(null, errors);
            }

            bool created = term is null;
            if (term is null)
            {
                term = new TaxonomyTerm
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    Vocabulary = vocabulary,
                    Code = code,
                    IsInterest = vocabulary == "interest",
                };
                db.TaxonomyTerms.Add(term);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            term.ParentId = parent;
            term.SortOrder = order;
            await db.TaxonomyTermNames.Where(n => n.TermId == term.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.TaxonomyTermNames.AddRange(names.Select(n => new TaxonomyTermName { TermId = term.Id, Locale = n.Key, Name = n.Value }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (!created)
            {
                await QueueChangedAsync(term.Id, ct).ConfigureAwait(false);
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = created ? "taxonomy.term_created" : "taxonomy.term_updated",
                ActorId = actor,
                SubjectKind = "taxonomy_term",
                SubjectIds = [term.Id],
                Detail = new Dictionary<string, string> { ["vocabulary"] = vocabulary, ["code"] = code },
            }, ct).ConfigureAwait(false);
            return new TermSaveOutcome(term.Id, new Dictionary<string, Phrase[]>());
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retires or restores a term. A category with active interests is not retired: they would be left without a heading.</summary>
    public Task<Result> SetActiveAsync(Guid termId, bool active, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            TaxonomyTerm? term = await db.TaxonomyTerms.FirstOrDefaultAsync(t => t.Id == termId, ct).ConfigureAwait(false);
            if (term is null || !IsManaged(term.Vocabulary))
            {
                return Result.Failure(Error.NotFound("taxonomy.not_found", "That term does not exist."));
            }

            if (term.IsActive == active)
            {
                return Result.Success();
            }

            if (!active && await db.TaxonomyTerms.AnyAsync(t => t.ParentId == termId && t.IsActive, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("taxonomy.has_active_children", "Retire or move this category's interests first."));
            }

            if (active && term.ParentId is { } parent
                && !await db.TaxonomyTerms.AnyAsync(t => t.Id == parent && t.IsActive, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("taxonomy.parent_retired", "Restore its category first."));
            }

            term.IsActive = active;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await QueueChangedAsync(term.Id, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = active ? "taxonomy.term_restored" : "taxonomy.term_retired",
                ActorId = actor,
                SubjectKind = "taxonomy_term",
                SubjectIds = [term.Id],
                Detail = new Dictionary<string, string> { ["vocabulary"] = term.Vocabulary, ["code"] = term.Code },
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The form field for a locale's name.</summary>
    public static string NameField(string locale) => "Name_" + locale;

    private Task<bool> QueueChangedAsync(Guid termId, CancellationToken ct) =>
        db.EnqueueJobAsync(new JobRequest
        {
            JobType = TermChangedJob.JobType,
            TenantId = tenant.TenantId,
            Payload = new TermChangedJob.Payload(termId),
            UniqueKey = TermChangedJob.UniqueKey(termId),
        }, ct);

    private static TermRow Row(TaxonomyTerm t, IEnumerable<TaxonomyTermName> names) =>
        new(t.Id, t.Code, t.ParentId, t.SortOrder, t.IsActive, names.ToDictionary(n => n.Locale, n => n.Name, StringComparer.Ordinal));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex CodePattern();
}
