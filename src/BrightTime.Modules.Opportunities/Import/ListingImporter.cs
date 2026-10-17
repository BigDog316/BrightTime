using System.Text;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Text;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Import;

/// <summary>The two file formats an organisation can import.</summary>
public enum ImportFormat
{
    Csv,
    Hsds,
}

/// <summary>What an applied import does with a row.</summary>
public enum ImportAction
{
    Create,
    Update,

    /// <summary>The listing it would update is waiting for review, or past editing.</summary>
    Skip,

    /// <summary>The row has errors and is not imported.</summary>
    Invalid,
}

/// <summary>One row's line in the dry-run report.</summary>
public sealed record ImportRowReport(int Row, string ExternalId, string Title, ImportAction Action, IReadOnlyList<ImportProblem> Errors);

/// <summary>A row's problem, and the column (or HSDS field) it is in. "*" is the whole row.</summary>
public sealed record ImportProblem(string Column, Phrase Message);

/// <summary>A dry run: why the file cannot be read, or what each row would do.</summary>
public sealed record ImportReport(Phrase? Problem, IReadOnlyList<ImportRowReport> Rows)
{
    public int Count(ImportAction action) => Rows.Count(r => r.Action == action);

    public bool CanApply => Problem is null && Rows.Any(r => r.Action is ImportAction.Create or ImportAction.Update);
}

/// <summary>
/// Bulk import: a CSV or HSDS file is read, every row checked as the editor would check it,
/// and the report shown; applying writes the valid rows as drafts or updates in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Stateless: apply re-reads and re-checks the same file rather than trusting an earlier
/// report, so a site closed or a listing submitted in between is caught. The file travels
/// back with the confirmation, protected by the caller.
/// </para>
/// <para>
/// A row never publishes anything. New listings are drafts; an update to a published listing
/// goes live and enters the organiser's "changed since review" lane, as an edit does (D15).
/// </para>
/// </remarks>
public sealed class ListingImporter(
    OpportunitiesDbContext db,
    ListingEditor editor,
    IDirectoryQueries directory,
    ITaxonomyQueries taxonomy)
{
    public const int MaxBytes = 1024 * 1024;

    public const int MaxRows = 500;

    public const int MaxExternalId = 200;

    public static string SourceOf(ImportFormat format) => format == ImportFormat.Csv ? "csv_import" : "hsds_import";

    public async Task<ImportReport> DryRunAsync(OrganisationId organisation, ImportFormat format, string content, Guid? defaultCategory, CancellationToken cancellationToken = default) =>
        (await CheckAsync(organisation, format, content, defaultCategory, cancellationToken).ConfigureAwait(false)).Report;

    /// <summary>Checks the file again and writes its valid rows. Nothing is written when the file cannot be read.</summary>
    public async Task<(ImportReport Report, ImportOutcome? Outcome)> ApplyAsync(
        OrganisationId organisation,
        ImportFormat format,
        string content,
        Guid? defaultCategory,
        UserId actor,
        CancellationToken cancellationToken = default)
    {
        (ImportReport report, List<(string, ParsedListing)> valid) = await CheckAsync(organisation, format, content, defaultCategory, cancellationToken).ConfigureAwait(false);
        if (!report.CanApply)
        {
            return (report, null);
        }

        ImportOutcome outcome = await editor.ImportAsync(organisation, SourceOf(format), valid, actor, cancellationToken).ConfigureAwait(false);
        return (report, outcome);
    }

    private async Task<(ImportReport Report, List<(string, ParsedListing)> Valid)> CheckAsync(
        OrganisationId organisation,
        ImportFormat format,
        string content,
        Guid? defaultCategory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
        {
            return (new ImportReport(Phrase.Of("import.error.file_too_large", ("max", MaxBytes / 1024)), []), []);
        }

        IReadOnlyList<ImportRecord>? records;
        Phrase? problem;
        if (format == ImportFormat.Csv)
        {
            (IReadOnlyList<CsvRecord>? table, int? badLine) = CsvTable.Read(content);
            if (table is null)
            {
                return (new ImportReport(Phrase.Of("import.error.bad_csv", ("line", badLine ?? 0)), []), []);
            }

            (records, problem) = CsvListingFormat.Map(table);
        }
        else
        {
            (records, problem) = HsdsListingFormat.Map(content);
        }

        if (records is null)
        {
            return (new ImportReport(problem ?? Phrase.Of("import.error.no_rows"), []), []);
        }

        if (records.Count > MaxRows)
        {
            return (new ImportReport(Phrase.Of("import.error.too_many_rows", ("max", MaxRows), ("count", records.Count)), []), []);
        }

        Lookups lookups = await LoadLookupsAsync(organisation, format, records, ct).ConfigureAwait(false);
        if (defaultCategory is { } fallback && !lookups.Categories.ContainsValue(fallback))
        {
            return (new ImportReport(Phrase.Of("import.error.default_category"), []), []);
        }

        List<ImportRowReport> rows = [];
        List<(string, ParsedListing)> valid = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ImportRecord record in records)
        {
            Resolve(record, format, lookups, defaultCategory);

            if (record.ExternalId.Length == 0)
            {
                record.Fail("id", Phrase.Of("import.error.id_required"));
            }
            else if (record.ExternalId.Length > MaxExternalId)
            {
                record.Fail("id", Phrase.Of("editor.error.too_long", ("max", MaxExternalId), ("length", record.ExternalId.Length)));
            }
            else if (!seen.Add(record.ExternalId))
            {
                record.Fail("id", Phrase.Of("import.error.duplicate_id", ("id", record.ExternalId)));
            }

            List<ImportProblem> problems = [.. record.Errors.SelectMany(e => e.Value.Select(m => new ImportProblem(e.Key, m)))];
            ParsedListing? parsed = null;
            if (!record.Errors.ContainsKey("*"))
            {
                (parsed, IReadOnlyDictionary<string, Phrase[]> fieldErrors) = ListingFormParser.Parse(record.Form);
                problems.AddRange(fieldErrors.SelectMany(e => e.Value.Select(m => new ImportProblem(ColumnOf(format, e.Key), m))));
            }

            ImportAction action;
            if (problems.Count > 0 || parsed is null)
            {
                action = ImportAction.Invalid;
            }
            else if (lookups.Existing.TryGetValue(record.ExternalId, out string? status))
            {
                action = ListingEditor.IsEditable(ListingEditor.ParseStatus(status)) ? ImportAction.Update : ImportAction.Skip;
            }
            else
            {
                action = ImportAction.Create;
            }

            if (action is ImportAction.Create or ImportAction.Update)
            {
                valid.Add((record.ExternalId, parsed!));
            }

            rows.Add(new ImportRowReport(record.Row, record.ExternalId, record.Form.Title.Trim(), action, problems));
        }

        return (new ImportReport(null, rows), valid);
    }

    private sealed record Lookups(
        Dictionary<string, Guid> Sites,
        Dictionary<string, Guid> Categories,
        Dictionary<string, Guid> Interests,
        Dictionary<string, Guid> Accommodations,
        Dictionary<string, string> Existing);

    private async Task<Lookups> LoadLookupsAsync(OrganisationId organisation, ImportFormat format, IReadOnlyList<ImportRecord> records, CancellationToken ct)
    {
        // Only the organisation's own open sites can be named; a closed one is reported as unknown.
        IReadOnlyList<SiteSummary> sites = await directory.GetOrganisationSitesAsync(organisation, ct).ConfigureAwait(false);
        Dictionary<string, Guid> siteByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (SiteSummary site in sites.Where(s => s.IsActive))
        {
            siteByName.TryAdd(site.Name.Trim(), site.SiteId);
        }

        async Task<Dictionary<string, Guid>> Vocabulary(string name) =>
            (await taxonomy.GetVocabularyAsync(name, "en", ct).ConfigureAwait(false))
                .ToDictionary(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);

        string source = SourceOf(format);
        string[] ids = [.. records.Select(r => r.ExternalId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        Dictionary<string, string> existing = await db.InTransactionAsync(c => db.Listings.AsNoTracking()
            .Where(l => l.OrgId == organisation.Value && l.Source == source && l.SourceRecordId != null && ids.Contains(l.SourceRecordId))
            .ToDictionaryAsync(l => l.SourceRecordId!, l => l.Status, StringComparer.Ordinal, c), ct).ConfigureAwait(false);

        return new Lookups(siteByName, await Vocabulary("category").ConfigureAwait(false), await Vocabulary("interest").ConfigureAwait(false),
            await Vocabulary("accommodation").ConfigureAwait(false), existing);
    }

    /// <summary>Turns the file's site names and term codes into the ids the editor's form carries.</summary>
    private static void Resolve(ImportRecord record, ImportFormat format, Lookups lookups, Guid? defaultCategory)
    {
        ListingForm form = record.Form;
        foreach (string name in record.SiteNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (lookups.Sites.TryGetValue(name, out Guid site))
            {
                form.SiteIds.Add(site);
            }
            else
            {
                record.Fail(format == ImportFormat.Csv ? "sites" : "service_at_locations", Phrase.Of("import.error.unknown_site", ("value", name)));
            }
        }

        bool strict = format == ImportFormat.Csv;
        void Terms(IEnumerable<string> codes, IReadOnlyList<Dictionary<string, Guid>> vocabularies, IList<Guid> target, string column)
        {
            foreach (string code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Guid? id = vocabularies.Select(v => v.TryGetValue(code, out Guid t) ? t : (Guid?)null).FirstOrDefault(t => t is not null);
                if (id is { } found)
                {
                    if (!target.Contains(found))
                    {
                        target.Add(found);
                    }
                }
                else if (strict)
                {
                    record.Fail(column, Phrase.Of("import.error.unknown_term", ("value", code)));
                }
            }
        }

        // HSDS terms may be categories or interests; CSV says which column each is in.
        Terms(record.CategoryCodes, strict ? [lookups.Categories] : [lookups.Categories, lookups.Interests], form.TermIds, "categories");
        Terms(record.InterestCodes, [lookups.Interests], form.TermIds, "interests");
        Terms(record.AccommodationCodes, [lookups.Accommodations], form.AccommodationIds, "accommodations");

        if (defaultCategory is { } fallback && !form.TermIds.Any(lookups.Categories.ContainsValue))
        {
            form.TermIds.Insert(0, fallback);
        }
    }

    private static string ColumnOf(ImportFormat format, string field) =>
        format == ImportFormat.Csv && CsvListingFormat.ColumnForField.TryGetValue(field, out string? column) ? column : field;
}
