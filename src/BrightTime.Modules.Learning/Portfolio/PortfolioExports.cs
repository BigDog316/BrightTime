using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Learning.Badges;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Portfolio;

public static class PortfolioExportFormats
{
    public const string Pdf = "pdf";
    public const string OpenBadges = "json_ob3";
    public const string Clr = "clr2";

    public static IReadOnlyList<string> All { get; } = [Pdf, OpenBadges, Clr];
}

[YouthData]
public sealed record ExportedActivity(string Title, string? Organisation, DateOnly StartedOn, DateOnly? EndedOn, int Sessions, decimal Hours);

[YouthData]
public sealed record ExportedBadge(string Name, string Issuer, DateTimeOffset IssuedOn, DateTimeOffset? ExpiresOn, Uri Verify);

/// <summary>What a portfolio export says: the young person's own choices and unhidden records, and the badges they hold.</summary>
[YouthData]
public sealed record PortfolioFacts(
    string? DisplayName, string? Headline, string? Bio, decimal TotalHours, bool HasHidden,
    IReadOnlyList<ExportedActivity> Activities, IReadOnlyList<ExportedBadge> Badges);

public sealed record ExportFile(byte[] Bytes, string ContentType, string FileName);

/// <summary>
/// A young person's portfolio, exported (M4, D68): made when asked and never kept, as reports
/// are. The PDF is worded and laid out by the host; this gives it the facts. The two credential
/// formats are made here: the badges as OB 3.0, and a CLR 2.0 the county's site signs.
/// </summary>
/// <remarks>
/// Only what the young person shows goes out: records they've hidden stay out, as they do from a
/// shown portfolio, and badges that were revoked are left out. Their legal name never appears; the
/// CLR identifies them as their badges do, by a salted hash.
/// </remarks>
public sealed class PortfolioExports(LearningDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, Badges.Badges badges)
{
    public const string Exported = "learning.portfolio.exported";

    public const string ClrContext = "https://purl.imsglobal.org/spec/clr/v2p0/context-2.0.1.json";
    public const string ClrSchema = "https://purl.imsglobal.org/spec/clr/v2p0/schema/json/clr_v2p0_clrcredential_schema.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static Error? Check(string? format) =>
        format is not null && PortfolioExportFormats.All.Contains(format)
            ? null
            : Error.Validation("portfolio.export_format", "pdf, json_ob3 or clr2.");

    /// <summary>The facts for the printable portfolio. The export is recorded.</summary>
    public Task<PortfolioFacts> FactsAsync(UserId user, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            await RecordAsync(user, PortfolioExportFormats.Pdf, ct).ConfigureAwait(false);
            return await GatherAsync(user, site, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>The badges as OB 3.0 credentials, or a CLR 2.0 holding them. The PDF is the host's to make, from <see cref="FactsAsync"/>.</summary>
    public Task<Result<ExportFile>> CredentialsAsync(UserId user, Uri site, string siteName, string format, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            if (format is not (PortfolioExportFormats.OpenBadges or PortfolioExportFormats.Clr))
            {
                return Result<ExportFile>.Failure(Error.Validation("portfolio.export_format", "json_ob3 or clr2."));
            }

            var held = await (from a in db.BadgeAssertions.AsNoTracking()
                              join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                              join i in db.Issuers on c.IssuerId equals i.Id
                              where a.RecipientUserId == user.Value && a.RevokedAt == null && a.CredentialJwt != null
                              orderby a.IssuedOn
                              select new { a.VerifySlug, a.CredentialJwt, a.CredentialJson, c.Name, Issuer = i.Name }).ToListAsync(ct).ConfigureAwait(false);
            string day = clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (format == PortfolioExportFormats.OpenBadges)
            {
                JsonArray credentials = [];
                foreach (var h in held)
                {
                    credentials.Add(new JsonObject
                    {
                        ["name"] = h.Name,
                        ["issuer"] = h.Issuer,
                        ["verify"] = BadgeDocuments.Credential(site, h.VerifySlug).ToString(),
                        ["jwt"] = h.CredentialJwt,
                        ["credential"] = h.CredentialJson is null ? null : JsonNode.Parse(h.CredentialJson),
                    });
                }

                JsonObject bundle = new() { ["exported"] = BadgeDocuments.Instant(clock.UtcNow), ["credentials"] = credentials };
                await RecordAsync(user, format, ct).ConfigureAwait(false);
                return Result<ExportFile>.Success(new ExportFile(Encoding.UTF8.GetBytes(bundle.ToJsonString(Indented)), "application/json", $"badges-{day}.json"));
            }

            // CLR 2.0 holds at least one verifiable credential.
            if (held.Count == 0)
            {
                return Result<ExportFile>.Failure(Error.Conflict("portfolio.nothing_to_export", "A learner record holds at least one badge. You don't hold one yet."));
            }

            PortfolioFacts facts = await GatherAsync(user, site, ct).ConfigureAwait(false);
            Guid exportId = await RecordAsync(user, format, ct).ConfigureAwait(false);
            List<ParticipationRecordRecord> records = await db.Participation.AsNoTracking().Where(p => p.UserId == user.Value && !p.IsHidden)
                .OrderBy(p => p.StartedOn).ToListAsync(ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            string jwt = await badges.SignAsSiteAsync(site, siteName, now, did => Clr(did, site, siteName, exportId, now, user, facts, records, [.. held.Select(h => h.CredentialJwt!)]), ct)
                .ConfigureAwait(false);
            return Result<ExportFile>.Success(new ExportFile(Encoding.ASCII.GetBytes(jwt), "application/jwt", $"learner-record-{day}.jwt"));
        }, cancellationToken);

    /// <summary>
    /// A <c>ClrCredential</c>: the young person's activities as co-curricular achievements, and
    /// their badges nested as the compact JWS strings they were signed as.
    /// </summary>
    private static JsonObject Clr(
        string did, Uri site, string siteName, Guid exportId, DateTimeOffset now, UserId user, PortfolioFacts facts,
        IReadOnlyList<ParticipationRecordRecord> records, IReadOnlyList<string> credentials)
    {
        string salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        JsonArray achievements = [];
        foreach (ParticipationRecordRecord r in records)
        {
            string hours = r.Hours.ToString("0.##", CultureInfo.InvariantCulture);
            string when = r.EndedOn is { } end ? $"{r.StartedOn:yyyy-MM-dd} to {end:yyyy-MM-dd}" : $"from {r.StartedOn:yyyy-MM-dd}";
            achievements.Add(new JsonObject
            {
                ["id"] = $"urn:uuid:{r.Id}",
                ["type"] = new JsonArray("Achievement"),
                ["achievementType"] = "CoCurricular",
                ["name"] = r.TitleSnapshot,
                ["description"] = r.OrgNameSnapshot is { } org ? $"{r.TitleSnapshot}, with {org}, {when}." : $"{r.TitleSnapshot}, {when}.",
                ["criteria"] = new JsonObject { ["narrative"] = $"Attended {r.SessionsAttended} sessions, {hours} hours, as recorded by the organisation." },
            });
        }

        JsonArray nested = [];
        foreach (string jwt in credentials)
        {
            nested.Add(jwt);
        }

        return new JsonObject
        {
            ["@context"] = new JsonArray(BadgeDocuments.CredentialsContext, ClrContext, BadgeDocuments.OpenBadgesContext),
            ["id"] = $"urn:uuid:{exportId}",
            ["type"] = new JsonArray("VerifiableCredential", "ClrCredential"),
            ["issuer"] = new JsonObject { ["id"] = did, ["type"] = new JsonArray("Profile"), ["name"] = siteName, ["url"] = site.ToString() },
            ["name"] = facts.DisplayName is { } name ? $"{name}: learner record" : "Learner record",
            ["description"] = $"Activities and badges recorded on {siteName}.",
            ["validFrom"] = BadgeDocuments.Instant(now),
            ["awardedDate"] = BadgeDocuments.Instant(now),
            ["partial"] = facts.HasHidden,
            ["credentialSubject"] = new JsonObject
            {
                ["type"] = new JsonArray("ClrSubject"),
                ["identifier"] = new JsonArray(new JsonObject
                {
                    ["type"] = "IdentityObject",
                    ["identityHash"] = BadgeDocuments.IdentityHashText(BadgeDocuments.RecipientIdentity(user.Value), salt),
                    ["identityType"] = "identifier",
                    ["hashed"] = true,
                    ["salt"] = salt,
                }),
                ["achievement"] = achievements,
                ["verifiableCredential"] = nested,
            },
            ["credentialSchema"] = new JsonArray(new JsonObject { ["id"] = ClrSchema, ["type"] = "1EdTechJsonSchemaValidator2019" }),
        };
    }

    private async Task<PortfolioFacts> GatherAsync(UserId user, Uri site, CancellationToken ct)
    {
        LearnerProfileRecord? profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
        List<ParticipationRecordRecord> records = await db.Participation.AsNoTracking().Where(p => p.UserId == user.Value)
            .OrderByDescending(p => p.StartedOn).ToListAsync(ct).ConfigureAwait(false);
        var held = await (from a in db.BadgeAssertions.AsNoTracking()
                          join c in db.BadgeClasses on a.BadgeClassId equals c.Id
                          join i in db.Issuers on c.IssuerId equals i.Id
                          where a.RecipientUserId == user.Value && a.RevokedAt == null
                          orderby a.IssuedOn descending
                          select new { a.VerifySlug, a.IssuedOn, a.ExpiresOn, c.Name, Issuer = i.Name }).ToListAsync(ct).ConfigureAwait(false);
        List<ParticipationRecordRecord> shown = [.. records.Where(r => !r.IsHidden)];
        return new PortfolioFacts(
            profile?.DisplayName, profile?.Headline, profile?.Bio, shown.Sum(r => r.Hours), records.Count != shown.Count,
            [.. shown.Select(r => new ExportedActivity(r.TitleSnapshot, r.OrgNameSnapshot, r.StartedOn, r.EndedOn, r.SessionsAttended, r.Hours))],
            [.. held.Select(h => new ExportedBadge(h.Name, h.Issuer, h.IssuedOn, h.ExpiresOn, BadgeDocuments.Credential(site, h.VerifySlug)))]);
    }

    private async Task<Guid> RecordAsync(UserId user, string format, CancellationToken ct)
    {
        DateTimeOffset now = clock.StoredNow();
        PortfolioExportRecord export = new()
        {
            Id = UuidV7.NewId(),
            TenantId = tenant.TenantId.Value,
            UserId = user.Value,
            Format = format,
            RequestedAt = now,
            CompletedAt = now,
        };
        db.Exports.Add(export);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = Exported,
            SubjectKind = "learner_profile",
            SubjectIds = [user.Value],
            Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["format"] = format },
        }, ct).ConfigureAwait(false);
        return export.Id;
    }
}
