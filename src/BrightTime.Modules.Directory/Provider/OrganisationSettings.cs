using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Directory.Review;
using BrightTime.Modules.Directory.Vetting;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Provider;

/// <summary>An organisation's profile as its admins see it: the form, where vetting stands, and what the organiser said.</summary>
public sealed record OrganisationProfileView(OrganisationForm Form, OrganisationStatus Status, string? ReviewNote, string Slug, uint Version);

/// <summary>
/// The organisation's own profile, edited by its owners and admins, and its side of vetting:
/// sending an application back after changes were asked for, or withdrawing it.
/// </summary>
/// <remarks>
/// Edits do not reset vetting: an approved organisation stays approved, and the organiser sees
/// every change in the audit log. An application that was sent back must be resubmitted, which
/// puts it in the queue again (<see cref="OrganisationVetting"/>).
/// </remarks>
public sealed class OrganisationSettings(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public Task<OrganisationProfileView?> LoadAsync(Guid organisation, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            // xmin is read in the query: a shadow property of an untracked entity has no value.
            var row = await db.Organisations.AsNoTracking()
                .Where(x => x.Id == organisation)
                .Select(x => new { Organisation = x, Version = EF.Property<uint>(x, "xmin") })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            Organisation? o = row?.Organisation;
            return o is null
                ? null
                : new OrganisationProfileView(
                    new OrganisationForm
                    {
                        DisplayName = o.DisplayName,
                        LegalName = o.LegalName,
                        Kind = o.OrgKind,
                        WebsiteUrl = o.WebsiteUrl ?? "",
                        Mission = o.Mission ?? "",
                        PublicEmail = o.PublicEmail ?? "",
                        PublicPhone = o.PublicPhone ?? "",
                        Languages = o.LanguagesServed,
                    },
                    o.ResolvedStatus, o.ReviewNote, o.Slug, row!.Version);
        }, cancellationToken);

    /// <summary>Saves the profile. The form must already have passed <see cref="ProviderForms.Check(OrganisationForm)"/>.</summary>
    /// <returns>False when someone else saved it since <paramref name="version"/> was read.</returns>
    public async Task<bool> SaveAsync(Guid organisation, OrganisationForm form, uint version, UserId actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        try
        {
            return await db.InTransactionAsync(async ct =>
            {
                Organisation o = await db.Organisations.FirstAsync(x => x.Id == organisation, ct).ConfigureAwait(false);
                db.Entry(o).Property<uint>("xmin").OriginalValue = version;
                o.DisplayName = form.DisplayName.Trim();
                o.LegalName = form.LegalName.Trim();
                o.OrgKind = form.Kind;
                o.WebsiteUrl = ProviderForms.Blank(form.WebsiteUrl);
                o.Mission = ProviderForms.Blank(form.Mission);
                o.PublicEmail = ProviderForms.Blank(form.PublicEmail);
                o.PublicPhone = ProviderForms.Blank(form.PublicPhone);
                o.LanguagesServed = [.. form.Languages.Distinct(StringComparer.Ordinal)];
                o.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await audit.RecordInTransactionAsync(db, new AuditAccess
                {
                    Action = "organisation.profile_updated",
                    ActorId = actor,
                    ActorOrgId = new OrganisationId(organisation),
                    SubjectKind = "organisation",
                    SubjectIds = [organisation],
                }, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>The provider's own moves: resubmitting after changes were asked for, or withdrawing.</summary>
    public Task<Result> MoveAsync(Guid organisation, OrganisationStatus to, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Organisation o = await db.Organisations.FirstAsync(x => x.Id == organisation, ct).ConfigureAwait(false);
            OrganisationStatus from = o.ResolvedStatus;
            bool hasSite = await db.Sites.AnyAsync(s => s.OrgId == o.Id && s.IsActive, ct).ConfigureAwait(false);
            Result allowed = OrganisationVetting.CanTransition(from, to, OrganisationVetting.Mover.Provider,
                new OrganisationVetting.TransitionContext { HasSite = hasSite, HasActiveOwner = true });
            if (!allowed.IsSuccess)
            {
                return allowed;
            }

            DateTimeOffset now = clock.UtcNow;
            o.Status = to.ToDbValue();
            if (to == OrganisationStatus.Applied)
            {
                o.SubmittedAt = now;
            }

            db.VettingEvents.Add(new OrganisationVettingEvent
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = o.Id,
                FromStatus = from.ToDbValue(),
                ToStatus = to.ToDbValue(),
                ActorId = actor.Value,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.QueueOrganisationChangedAsync(tenant.TenantId, o.Id, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = "organisation.status_changed",
                ActorId = actor,
                ActorOrgId = new OrganisationId(organisation),
                SubjectKind = "organisation",
                SubjectIds = [organisation],
                Detail = new Dictionary<string, string> { ["from"] = from.ToDbValue(), ["to"] = to.ToDbValue() },
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);
}
