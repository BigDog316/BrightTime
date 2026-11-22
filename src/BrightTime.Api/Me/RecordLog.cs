using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Me;

/// <summary>
/// One time someone else looked at or changed a person's record (D96). <c>What</c> is a short
/// code the app words (<c>record_read</c>, <c>attendance_recorded</c>, ...; <c>other</c> for
/// anything not named). <c>Who</c> is what the actor was: <c>staff</c> of <c>Organisation</c>, in
/// <c>Role</c> and with any job <c>Title</c> while they're still a member; a <c>guardian</c>; an <c>organiser</c> of the site;
/// <c>public</c> (someone on the web, not signed in); <c>system</c> (BrightTime itself); or a
/// <c>person</c> with an account and none of those. <c>Basis</c> is the reason an organisation
/// had access, when one was checked. <c>Refused</c>: the attempt was turned away.
/// </summary>
[YouthData]
public sealed record RecordLogEntry(DateTimeOffset At, string What, bool Refused, string Who, string? Organisation, string? Role, string? Title, string? Basis);

/// <summary>A page of the log, newest first, and the cursor for older entries if there are more.</summary>
[YouthData]
public sealed record RecordLogPage(IReadOnlyList<RecordLogEntry> Entries, string? Older);

/// <summary>
/// "Who has seen my record" (D96, docs/plan/04's <c>audit.read</c>, own and children): every time
/// someone else read or changed a person's records, refusals included, from the audit trail.
/// </summary>
/// <remarks>
/// <para>
/// The trail names records by whatever id each module audits with, so every module that keeps
/// records about people says which are this person's (<see cref="IAuditSubjects"/>), and the log
/// reads any row naming one of them or the person. The person's own actions are left out, and so
/// are the viewer's when a guardian reads a child's.
/// </para>
/// <para>
/// Staff are named by their organisation, their role there and any job title they were invited
/// with: the system keeps no names for them, and their email addresses would be a way for a young
/// person to be contacted off the platform. Nothing about another young person is ever shown: an entry is about what was done
/// to this person's records, by whom, and why.
/// </para>
/// </remarks>
public sealed class RecordLog(
    AuditLog log, IEnumerable<IAuditSubjects> modules, IIdentityQueries identity, IDirectoryQueries directory, ITenantContext tenant)
{
    public const string Read = "identity.record_log.read";

    private static readonly string[] OrganiserRoles = [BrightTimeRoles.NetworkOrganiser, BrightTimeRoles.Moderator, BrightTimeRoles.PlatformAdmin];

    /// <summary>The person's own log.</summary>
    public Task<RecordLogPage> MineAsync(UserId person, string? older, CancellationToken cancellationToken = default) =>
        PageAsync(person, person, older, cancellationToken);

    /// <summary>A child profile's log, for their verified guardian, while it's still a child profile.</summary>
    public async Task<Result<RecordLogPage>> ChildAsync(UserId guardian, UserId child, string? older, CancellationToken cancellationToken = default)
    {
        GuardianRelationship? link = (await identity.GetChildrenAsync(guardian, cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.ChildUserId == child);
        if (link is null)
        {
            return Result<RecordLogPage>.Failure(Error.NotFound("record_log.child_not_found", "You aren't the verified guardian of a child profile like that."));
        }

        if (!link.CanViewActivity)
        {
            return Result<RecordLogPage>.Failure(Error.Forbidden("record_log.guardian_view", "Your link to this child doesn't include seeing what they take part in."));
        }

        if (!link.IsChildProfile)
        {
            return Result<RecordLogPage>.Failure(Error.Forbidden("record_log.child_claimed", "Their record is theirs now that they have their own account."));
        }

        return Result<RecordLogPage>.Success(await PageAsync(child, guardian, older, cancellationToken).ConfigureAwait(false));
    }

    private async Task<RecordLogPage> PageAsync(UserId person, UserId viewer, string? older, CancellationToken ct)
    {
        HashSet<Guid> subjects = [person.Value];
        foreach (IAuditSubjects module in modules)
        {
            subjects.UnionWith(await module.RecordsOfAsync(person, ct).ConfigureAwait(false));
        }

        AuditPage page = await log.SearchAsync(tenant.TenantId, viewer, new AuditFilter
        {
            AnySubject = subjects,
            NotActors = [person.Value, viewer.Value],
        }, older, ct).ConfigureAwait(false);

        Names names = new();
        List<RecordLogEntry> entries = [];
        foreach (AuditRow row in page.Rows)
        {
            (string who, string? organisation, string? role, string? title) = await WhoAsync(row, person, names, ct).ConfigureAwait(false);
            entries.Add(new RecordLogEntry(
                row.OccurredAt, What(row.Action), row.Outcome == AuditOutcome.Deny, who, organisation, role, title,
                row.Detail.TryGetValue("basis", out string? basis) ? basis : null));
        }

        return new RecordLogPage(entries, page.Older);
    }

    /// <summary>What a page has already looked up, so each actor and organisation is asked about once.</summary>
    private sealed class Names
    {
        public Dictionary<Guid, string?> Organisations { get; } = [];

        public Dictionary<(Guid Actor, Guid Org), (string? Role, string? Title)> Roles { get; } = [];

        public Dictionary<Guid, string> People { get; } = [];
    }

    private async Task<(string Who, string? Organisation, string? Role, string? Title)> WhoAsync(AuditRow row, UserId person, Names names, CancellationToken ct)
    {
        if (row.ActorKind is "anonymous")
        {
            return ("public", null, null, null);
        }

        if (row.ActorId is not { } actor || row.ActorKind is not "user")
        {
            return ("system", null, null, null);
        }

        if (row.ActorOrgId is { } org)
        {
            if (!names.Organisations.TryGetValue(org, out string? name))
            {
                name = await directory.GetOrganisationNameAsync(new OrganisationId(org), ct).ConfigureAwait(false);
                names.Organisations[org] = name;
            }

            // The role and title they hold now; null once they've left, and the organisation is named alone.
            if (!names.Roles.TryGetValue((actor, org), out (string? Role, string? Title) member))
            {
                OrganisationId id = new(org);
                member = (await directory.GetOrganisationRoleAsync(id, new UserId(actor), ct).ConfigureAwait(false),
                          await directory.GetMemberTitleAsync(id, new UserId(actor), ct).ConfigureAwait(false));
                names.Roles[(actor, org)] = member;
            }

            return ("staff", name, member.Role, member.Title);
        }

        if (!names.People.TryGetValue(actor, out string? who))
        {
            who = await identity.IsVerifiedGuardianOfAsync(new UserId(actor), person, ct).ConfigureAwait(false)
                ? "guardian"
                : (await identity.GetRoleNamesAsync(new UserId(actor), ct).ConfigureAwait(false)).Any(r => OrganiserRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
                    ? "organiser"
                    : "person";
            names.People[actor] = who;
        }

        return (who, null, null, null);
    }

    /// <summary>The audit action as one of the few things the app words; anything else is <c>other</c>.</summary>
    public static string What(string action) => action switch
    {
        "identity.youth_record.read" => "record_read",
        "attendance.roster.read" => "attendance_list_read",
        "attendance.read" or "attendance.family_read" => "attendance_read",
        "attendance.recorded" or "attendance.synced" => "attendance_recorded",
        "attendance.corrected" => "attendance_corrected",
        "provider.roster" or "provider.attendance" or "provider.participation" => "report_made",
        "enrollment.application.read" or "enrollment.application.org_read" => "application_read",
        "enrollment.application.changed" => "application_changed",
        "enrollment.application.decided" => "application_decided",
        "enrollment.family.read" or "enrollment.family_share.read" => "family_read",
        "enrollment.family.changed" => "family_changed",
        "enrollment.waiver.signed" => "waiver_signed",
        "learning.badge.read" => "badges_read",
        "learning.badge.issued" => "badge_awarded",
        "learning.badge.revoked" => "badge_revoked",
        "learning.playlist.read" => "playlist_read",
        "learning.playlist.progressed" => "playlist_progressed",
        "learning.playlist.reviewed" => "playlist_reviewed",
        "learning.portfolio.read" or "learning.portfolio.guardian_read" => "portfolio_read",
        "learning.portfolio.public_read" => "portfolio_public_read",
        "learning.portfolio.changed" => "portfolio_changed",
        "learning.portfolio.exported" => "portfolio_exported",
        "learning.project.read" => "projects_read",
        "learning.project.changed" => "project_changed",
        "learning.project.reviewed" => "project_reviewed",
        "learning.skill.read" or "learning.skill.endorsements_read" => "skills_read",
        "learning.skill.changed" => "skill_changed",
        "learning.skill.endorsed" => "skill_endorsed",
        "learning.goal.read" => "goals_read",
        "learning.goal.changed" => "goals_changed",
        "consent.child_created" => "profile_created",
        "consent.granted" => "consent_granted",
        "consent.revoked" => "consent_revoked",
        "consent.link_verified" => "link_verified",
        "consent.link_revoked" => "link_revoked",
        "consent.email_sent" or "consent.confirmation_sent" or "consent.claim_email_sent" => "account_email",
        "consent.claim_requested" => "claim_requested",
        Read => "record_log_read",
        _ => "other",
    };
}
