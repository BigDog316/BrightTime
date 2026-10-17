using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Point = NetTopologySuite.Geometries.Point;

namespace BrightTime.Modules.Directory.Provider;

/// <summary>
/// A new organisation asking to be listed: the organisation, its first site, and its owner's
/// membership, written together.
/// </summary>
/// <remarks>
/// <para>
/// The organisation starts as <c>applied</c> and joins the organiser's queue; nothing it lists
/// can be published until it is approved (D15). The applicant is the owner. A new account's
/// membership is active at once, since only the address's owner can set its password. An
/// existing account's is an invitation, accepted after signing in, so typing someone else's
/// address makes them owner of nothing (D23).
/// </para>
/// <para>
/// The site is placed at its neighbourhood's centre (<c>locality</c> accuracy) until a geocoder
/// is chosen (Q7): close enough for distance search, honest about how close.
/// </para>
/// </remarks>
public sealed class OrganisationApplications(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public Task<OrganisationId> ApplyAsync(
        OrganisationForm organisation,
        SiteForm site,
        Coordinate siteLocation,
        UserId applicant,
        bool applicantIsActive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organisation);
        ArgumentNullException.ThrowIfNull(site);

        return db.InTransactionAsync(async ct =>
        {
            DateTimeOffset now = clock.UtcNow;
            Guid orgId = UuidV7.NewId();
            string slug = await UniqueSlugAsync(organisation.DisplayName, ct).ConfigureAwait(false);

            db.Organisations.Add(new Organisation
            {
                Id = orgId,
                TenantId = tenant.TenantId.Value,
                DisplayName = organisation.DisplayName.Trim(),
                LegalName = organisation.LegalName.Trim(),
                Slug = slug,
                Status = OrganisationStatus.Applied.ToDbValue(),
                OrgKind = organisation.Kind,
                WebsiteUrl = ProviderForms.Blank(organisation.WebsiteUrl),
                Mission = ProviderForms.Blank(organisation.Mission),
                PublicEmail = ProviderForms.Blank(organisation.PublicEmail),
                PublicPhone = ProviderForms.Blank(organisation.PublicPhone),
                LanguagesServed = [.. organisation.Languages.Distinct(StringComparer.Ordinal)],
                SubmittedAt = now,
                PrimaryContactUserId = applicantIsActive ? applicant.Value : null,
            });

            // The model maps no relationships, so EF cannot order the inserts: the organisation
            // goes first, by itself, before the rows that reference it.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            db.Sites.Add(SiteEditor.NewSite(orgId, tenant.TenantId.Value, site, siteLocation));

            db.Memberships.Add(new OrgMembership
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = orgId,
                UserId = applicant.Value,
                OrgRole = MemberRoles.Owner,
                Status = applicantIsActive ? MembershipStatus.Active : MembershipStatus.Invited,
                InvitedAt = now,
                JoinedAt = applicantIsActive ? now : null,
            });

            db.VettingEvents.Add(new OrganisationVettingEvent
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = orgId,
                FromStatus = null,
                ToStatus = OrganisationStatus.Applied.ToDbValue(),
                OccurredAt = now,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Who typed the form is not known: the applicant's address may be anyone's. The row
            // names the organisation, and the account it was sent to only as a subject.
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "organisation.applied",
                SubjectKind = "organisation",
                SubjectIds = [orgId, applicant.Value],
                Detail = new Dictionary<string, string> { ["owner_membership"] = applicantIsActive ? "active" : "invited" },
            }, ct).ConfigureAwait(false);

            return new OrganisationId(orgId);
        }, cancellationToken);
    }

    private async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        string root = Slug.From(name, "organisation");
        string candidate = root;
        for (int n = 2; await db.Organisations.AnyAsync(o => o.Slug == candidate, ct).ConfigureAwait(false); n++)
        {
            string suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            candidate = (root.Length + suffix.Length > Slug.MaxLength ? root[..(Slug.MaxLength - suffix.Length)] : root) + suffix;
        }

        return candidate;
    }

    /// <summary>A point for the site from its neighbourhood's centre.</summary>
    internal static Point PointAt(Coordinate c) => new(c.Longitude, c.Latitude) { SRID = 4326 };
}
