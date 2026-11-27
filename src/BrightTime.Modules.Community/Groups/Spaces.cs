using System.Text.Json.Serialization;
using BrightTime.Abstractions.Screening;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Groups;

/// <summary>A space as a list shows it: what it is, how many are in it, and when it was last written in.</summary>
public sealed record SpaceSummary(Guid Id, Guid OrgId, string OrgName, Guid? ListingId, string Name, string? Description, string Status, int YouthCount, int StaffCount, DateTimeOffset? LastPostAt, bool IsMember);

/// <summary>
/// Someone in a space. A young person is named as the activity's roster names them (D44), or not
/// at all; staff are named by email to their organisation's staff only, and never to young people.
/// The account's id is for the console, and never leaves the server.
/// </summary>
[YouthData]
public sealed record SpaceMemberView([property: JsonIgnore] Guid UserId, string Role, string? Name, DateTimeOffset JoinedAt, bool IsMe);

/// <summary>
/// A post, or a reply. A held one shows only to its author, marked so, with <see cref="HeldFor"/>
/// the screen's reasons, and so does one a moderator removed.
/// </summary>
[YouthData]
public sealed record PostView(Guid Id, bool FromMe, string AuthorRole, string? AuthorName, string Body, string State, DateTimeOffset At, IReadOnlyList<string>? HeldFor, IReadOnlyList<PostView> Replies);

/// <summary>A space with its people and its posts, newest first, each with its replies oldest first.</summary>
[YouthData]
public sealed record SpaceView(Guid Id, Guid OrgId, string OrgName, Guid? ListingId, string Name, string? Description, string Status, bool IsMember, bool CanPost, IReadOnlyList<SpaceMemberView> Members, IReadOnlyList<PostView> Posts);

/// <summary>
/// Spaces (M5, D120): an activity's own place for its young people and the staff chosen for it to
/// post and reply, with the safety rules the schema enforces made the application's first step.
/// </summary>
/// <remarks>
/// <para>
/// An organisation's staff open one space per activity and choose its staff. Its young people
/// aren't chosen: everyone with their own account and a live enrolment in the activity whose
/// consent allows posting in a community (<c>can_post_in_community</c>, so nobody under 13) is a
/// member, and leaves when that ends. Membership is brought up to date whenever anyone opens the
/// space or a list of spaces, before anything is read or written.
/// </para>
/// <para>
/// No young person is in a space with fewer than two of its staff, and none with a member of
/// staff a contact restriction keeps from them: they wait until it's safe. The database refuses a
/// minor in a space with fewer than two adult staff anyway (<c>enforce_safe_space()</c>).
/// </para>
/// <para>
/// Everything a young person writes is screened for personal information (D117), and an adult's
/// writing in a space with young people for an attempt to take them off the platform (D53);
/// what's held waits for the county's moderators (<see cref="Moderation.HeldPosts"/>). A muted
/// adult (D119) doesn't write where young people read. Every read and write is audited.
/// </para>
/// </remarks>
public sealed class Spaces(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IEnrolmentQueries enrolments,
    IListingDetails listings,
    IScreeningService screener,
    IClearanceGate? clearances = null)
{
    public const string Read = "spaces.read";
    public const string Posted = "spaces.posted";
    public const string PostDeleted = "spaces.post_deleted";
    public const string Opened = "spaces.opened";
    public const string Archived = "spaces.archived";
    public const string StaffAdded = "spaces.staff_added";
    public const string StaffRemoved = "spaces.staff_removed";
    public const string MembershipChanged = "spaces.membership_changed";

    public const int MaxBody = 4000;
    public const int MaxName = 120;
    public const int MaxDescription = 1000;

    /// <summary>How many posts a space shows, newest first.</summary>
    public const int PageSize = 100;

    /// <summary>The fewest staff a space has before any young person joins it.</summary>
    public const int MinimumStaff = 2;

    private const string SubjectKind = "space";

    /// <summary>Opens a space for one of the organisation's activities, with its opener as its first member of staff.</summary>
    public Task<Result<Guid>> OpenAsync(OrganisationId org, UserId staff, Guid listingId, string? name, string? description, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if ((await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false)) is not { } listing || listing.OrganisationId != org)
            {
                return Result<Guid>.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            if (await identity.IsMinorAsync(staff, ct).ConfigureAwait(false))
            {
                return Result<Guid>.Failure(AdultsOnly());
            }

            string title = name?.Trim() is { Length: > 0 } n ? n : listing.Title.Trim();
            if (title.Length > MaxName)
            {
                return Result<Guid>.Failure(Error.Validation("spaces.name", $"A name of up to {MaxName} characters."));
            }

            string? about = description?.Trim() is { Length: > 0 } d ? d : null;
            if (about is { Length: > MaxDescription })
            {
                return Result<Guid>.Failure(Error.Validation("spaces.description", $"A description of up to {MaxDescription} characters."));
            }

            if (await db.Spaces.AnyAsync(s => s.ListingId == listingId && s.Status == SpaceStatuses.Active, ct).ConfigureAwait(false))
            {
                return Result<Guid>.Failure(Error.Conflict("spaces.exists", "This activity already has a space. Archive it to open another."));
            }

            DateTimeOffset now = clock.StoredNow();
            SpaceRecord space = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                ListingId = listingId,
                SpaceKind = SpaceKinds.Cohort,
                Name = title,
                Description = about,
                Status = SpaceStatuses.Active,
                CreatedBy = staff.Value,
                CreatedAt = now,
            };
            db.Spaces.Add(space);
            db.SpaceMembers.Add(Member(space, staff.Value, SpaceRoles.Staff, isMinor: false, now));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Opened, [space.Id], ct, org).ConfigureAwait(false);
            return Result<Guid>.Success(space.Id);
        }, cancellationToken);

    /// <summary>The organisation's spaces, open ones first, each brought up to date.</summary>
    public Task<IReadOnlyList<SpaceSummary>> ForOrganisationAsync(OrganisationId org, UserId viewer, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<SpaceSummary>>(async ct =>
        {
            List<SpaceRecord> spaces = await db.Spaces.Where(s => s.OrgId == org.Value)
                .OrderBy(s => s.Status == SpaceStatuses.Active ? 0 : 1).ThenBy(s => s.Name).ToListAsync(ct).ConfigureAwait(false);
            List<SpaceSummary> list = [];
            foreach (SpaceRecord space in spaces)
            {
                await ReconcileAsync(space, ct).ConfigureAwait(false);
                list.Add(await SummaryAsync(space, viewer, ct).ConfigureAwait(false));
            }

            return list;
        }, cancellationToken);

    /// <summary>The spaces a person is in, newest activity first; a young person's are those of the activities they're enrolled in.</summary>
    public Task<IReadOnlyList<SpaceSummary>> MineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<SpaceSummary>>(async ct =>
        {
            List<Guid> enrolled = [.. (await enrolments.ForPersonAsync(user.Value, ct).ConfigureAwait(false))
                .Where(e => e.Status == EnrollmentStatuses.Active).Select(e => e.ListingId).Distinct()];
            List<Guid> memberOf = await db.SpaceMembers.AsNoTracking().Where(m => m.UserId == user.Value && m.LeftAt == null)
                .Select(m => m.SpaceId).ToListAsync(ct).ConfigureAwait(false);
            List<SpaceRecord> candidates = await db.Spaces
                .Where(s => memberOf.Contains(s.Id) || (s.Status == SpaceStatuses.Active && s.ListingId != null && enrolled.Contains(s.ListingId.Value)))
                .ToListAsync(ct).ConfigureAwait(false);
            List<SpaceSummary> list = [];
            foreach (SpaceRecord space in candidates)
            {
                await ReconcileAsync(space, ct).ConfigureAwait(false);
                if (await ActiveMemberAsync(space.Id, user, ct).ConfigureAwait(false) is not null)
                {
                    list.Add(await SummaryAsync(space, user, ct).ConfigureAwait(false));
                }
            }

            await AuditAsync(Read, [.. list.Select(s => s.Id)], ct).ConfigureAwait(false);
            return [.. list.OrderByDescending(s => s.LastPostAt ?? DateTimeOffset.MinValue)];
        }, cancellationToken);

    /// <summary>A space, for one of its members.</summary>
    public Task<Result<SpaceView>> GetAsync(UserId user, Guid spaceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, ct).ConfigureAwait(false) is not { } space)
            {
                return Result<SpaceView>.Failure(NotFound());
            }

            IReadOnlyDictionary<Guid, string?> names = await ReconcileAsync(space, ct).ConfigureAwait(false);
            if (await ActiveMemberAsync(space.Id, user, ct).ConfigureAwait(false) is null)
            {
                return Result<SpaceView>.Failure(NotFound());
            }

            await AuditAsync(Read, [space.Id], ct).ConfigureAwait(false);
            return Result<SpaceView>.Success(await ViewAsync(space, user, names, forStaff: false, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// A space of the organisation, for a member of its staff whether or not they're in it: young
    /// people's posts in a space are visible to the organisation, as their conversations are (T1).
    /// The caller has checked <c>spaces.view</c> for the organisation.
    /// </summary>
    public Task<Result<SpaceView>> GetForOrganisationAsync(OrganisationId org, UserId staff, Guid spaceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && s.OrgId == org.Value, ct).ConfigureAwait(false) is not { } space)
            {
                return Result<SpaceView>.Failure(NotFound());
            }

            IReadOnlyDictionary<Guid, string?> names = await ReconcileAsync(space, ct).ConfigureAwait(false);
            await AuditAsync(Read, [space.Id], ct, org).ConfigureAwait(false);
            return Result<SpaceView>.Success(await ViewAsync(space, staff, names, forStaff: true, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>A member posts, or replies to a published post. What a young person writes is screened first; so is an adult's where young people read.</summary>
    public Task<Result<PostView>> PostAsync(UserId user, Guid spaceId, string? body, Guid? replyTo, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, ct).ConfigureAwait(false) is not { } space)
            {
                return Result<PostView>.Failure(NotFound());
            }

            IReadOnlyDictionary<Guid, string?> names = await ReconcileAsync(space, ct).ConfigureAwait(false);
            if (await ActiveMemberAsync(space.Id, user, ct).ConfigureAwait(false) is not { } me)
            {
                return Result<PostView>.Failure(NotFound());
            }

            if (space.Status != SpaceStatuses.Active)
            {
                return Result<PostView>.Failure(ArchivedError());
            }

            if (body?.Trim() is not { Length: > 0 and <= MaxBody } text)
            {
                return Result<PostView>.Failure(Error.Validation("spaces.body", $"A post of 1 to {MaxBody} characters."));
            }

            PostRecord? parent = null;
            if (replyTo is { } answering)
            {
                parent = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == answering && p.SpaceId == spaceId && p.ReplyToId == null && p.State == PostStates.Published, ct).ConfigureAwait(false);
                if (parent is null)
                {
                    return Result<PostView>.Failure(Error.NotFound("spaces.reply_not_found", "There's no post like that to reply to in this space."));
                }
            }

            bool minorsHere = await db.SpaceMembers.AnyAsync(m => m.SpaceId == spaceId && m.LeftAt == null && m.IsMinor, ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            if (!me.IsMinor && minorsHere && await Moderation.Restrictions.IsMutedAsync(db, user.Value, now, ct).ConfigureAwait(false))
            {
                return Result<PostView>.Failure(Error.Forbidden("spaces.muted",
                    "A moderator has paused your writing to young people for now, so you can't post in a space they're in."));
            }

            MessageScreen screen = me.IsMinor ? MessageScreen.YouthPii : minorsHere ? MessageScreen.ContactExchange : MessageScreen.None;
            ScreenVerdict? verdict = await ContentScreening.ScreenAsync(screener, text, screen, minorsHere, "post", ct).ConfigureAwait(false);
            bool published = verdict is null || verdict.Passed;
            PostRecord post = new()
            {
                Id = UuidV7.NewId(),
                TenantId = space.TenantId,
                SpaceId = space.Id,
                AuthorUserId = user.Value,
                AuthorIsMinor = me.IsMinor,
                Body = text,
                State = published ? PostStates.Published : PostStates.Held,
                ScreenResult = verdict?.ScreenResult,
                ScreenedAt = verdict is null ? null : now,
                PublishedAt = published ? now : null,
                HeldReason = published ? null : string.Join(',', verdict!.Reasons),
                ReplyToId = parent?.Id,
                CreatedAt = now,
            };
            db.Posts.Add(post);
            if (verdict is not null)
            {
                db.ScreeningResults.Add(verdict.Record(space.TenantId, "post", post.Id, now));
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (published && parent is not null)
            {
                await CountReplyAsync(db, parent.Id, +1, ct).ConfigureAwait(false);
            }

            await AuditAsync(Posted, [space.Id], ct, me.IsMinor ? null : new OrganisationId(space.OrgId)).ConfigureAwait(false);
            return Result<PostView>.Success(new PostView(post.Id, true, me.MemberRole, names.GetValueOrDefault(user.Value), text, post.State, now,
                published ? null : verdict!.Reasons, []));
        }, cancellationToken);

    /// <summary>The author takes down a post of their own, published or held; nobody sees it after.</summary>
    public Task<Result> DeleteAsync(UserId user, Guid spaceId, Guid postId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Posts.FirstOrDefaultAsync(p => p.Id == postId && p.SpaceId == spaceId && p.AuthorUserId == user.Value
                    && (p.State == PostStates.Published || p.State == PostStates.Held), ct).ConfigureAwait(false) is not { } post)
            {
                return Result.Failure(Error.NotFound("spaces.post_not_found", "You have no post like that in this space."));
            }

            bool wasPublished = post.State == PostStates.Published;
            post.State = PostStates.DeletedByAuthor;
            post.PublishedAt = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (wasPublished && post.ReplyToId is { } parentId)
            {
                await CountReplyAsync(db, parentId, -1, ct).ConfigureAwait(false);
            }

            await AuditAsync(PostDeleted, [spaceId], ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Adds a member of the organisation's staff: an adult, cleared where the county requires it, whom no restriction keeps from its young people.</summary>
    public Task<Result> AddStaffAsync(OrganisationId org, UserId actor, Guid spaceId, UserId person, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && s.OrgId == org.Value, ct).ConfigureAwait(false) is not { } space)
            {
                return Result.Failure(NotFound());
            }

            if (space.Status != SpaceStatuses.Active)
            {
                return Result.Failure(ArchivedError());
            }

            if ((await directory.GetOrganisationMembersAsync(org, ct).ConfigureAwait(false)).All(m => m.UserId != person))
            {
                return Result.Failure(Error.NotFound("spaces.not_staff", "That person isn't on your organisation's staff."));
            }

            if (await identity.IsMinorAsync(person, ct).ConfigureAwait(false))
            {
                return Result.Failure(AdultsOnly());
            }

            if (await ActiveMemberAsync(space.Id, person, ct).ConfigureAwait(false) is not null)
            {
                return Result.Failure(Error.Conflict("spaces.already_member", "They're in this space already."));
            }

            if (clearances is not null && await clearances.CheckAsync(person, org, ct).ConfigureAwait(false) is not null)
            {
                return Result.Failure(Error.Forbidden("spaces.not_cleared", "They don't hold the clearances the county requires to work with young people here."));
            }

            foreach (Guid youth in await db.SpaceMembers.Where(m => m.SpaceId == space.Id && m.LeftAt == null && m.MemberRole == SpaceRoles.Youth)
                .Select(m => m.UserId).ToListAsync(ct).ConfigureAwait(false))
            {
                if (await identity.IsContactRestrictedAsync(person, new UserId(youth), ct).ConfigureAwait(false))
                {
                    return Result.Failure(Error.Forbidden("spaces.restricted", "A contact restriction keeps them from a young person in this space."));
                }
            }

            db.SpaceMembers.Add(Member(space, person.Value, SpaceRoles.Staff, isMinor: false, clock.StoredNow()));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(StaffAdded, [space.Id], ct, org, person).ConfigureAwait(false);

            // With a second member of staff, the activity's young people may now join.
            await ReconcileAsync(space, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Takes a member of staff out, unless that would leave young people with fewer than two.</summary>
    public Task<Result> RemoveStaffAsync(OrganisationId org, UserId actor, Guid spaceId, UserId person, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId && s.OrgId == org.Value, ct).ConfigureAwait(false) is not { } space)
            {
                return Result.Failure(NotFound());
            }

            List<SpaceMemberRecord> members = await db.SpaceMembers.Where(m => m.SpaceId == space.Id && m.LeftAt == null).ToListAsync(ct).ConfigureAwait(false);
            if (members.FirstOrDefault(m => m.UserId == person.Value && m.MemberRole != SpaceRoles.Youth) is not { } leaving)
            {
                return Result.Failure(Error.NotFound("spaces.not_member", "They aren't on this space's staff."));
            }

            if (members.Any(m => m.IsMinor) && members.Count(m => m.MemberRole != SpaceRoles.Youth && !m.IsMinor) - 1 < MinimumStaff)
            {
                return Result.Failure(UnsafeShape());
            }

            leaving.LeftAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(StaffRemoved, [space.Id], ct, org, person).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Archives a space: it reads as it was and takes no more posts; its activity can have a new one.</summary>
    public Task<Result> ArchiveAsync(OrganisationId org, UserId actor, Guid spaceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && s.OrgId == org.Value, ct).ConfigureAwait(false) is not { } space)
            {
                return Result.Failure(NotFound());
            }

            if (space.Status != SpaceStatuses.Active)
            {
                return Result.Failure(ArchivedError());
            }

            space.Status = SpaceStatuses.Archived;
            space.ArchivedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Archived, [space.Id], ct, org).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- membership --------------------------------------------------------------------------

    /// <summary>
    /// Brings an open space's members up to date, and returns the activity's young people with
    /// their roster names (null when the family's share doesn't name them). Staff who've left the
    /// organisation leave the space; the activity's young people join while they're enrolled, can
    /// post in a community and no restriction keeps a member of staff from them, and only once the
    /// space has two staff; anyone else leaves. An archived space is left as it was.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, string?>> ReconcileAsync(SpaceRecord space, CancellationToken ct)
    {
        Dictionary<Guid, string?> enrolled = [];
        if (space.ListingId is { } listing)
        {
            IReadOnlyList<RosterEntry> roster = await enrolments.RosterAsync(listing, ct).ConfigureAwait(false);
            Dictionary<Guid, string?> named = roster.ToDictionary(r => r.EnrollmentId, r => r.DisplayName);
            DateOnly today = clock.Today;
            foreach (EnrolmentFacts facts in roster.Count == 0 ? [] : await enrolments.FactsAsync([.. named.Keys], today, ct).ConfigureAwait(false))
            {
                if (facts.YouthUserId is { } youth)
                {
                    enrolled[youth] = named.GetValueOrDefault(facts.EnrollmentId) ?? enrolled.GetValueOrDefault(youth);
                }
            }
        }

        if (space.Status != SpaceStatuses.Active)
        {
            return enrolled;
        }

        DateTimeOffset now = clock.StoredNow();
        List<SpaceMemberRecord> members = await db.SpaceMembers.Where(m => m.SpaceId == space.Id && m.LeftAt == null).ToListAsync(ct).ConfigureAwait(false);
        HashSet<Guid> staffOfOrg = [.. (await directory.GetOrganisationMembersAsync(new OrganisationId(space.OrgId), ct).ConfigureAwait(false)).Select(m => m.UserId.Value)];
        int joined = 0, left = 0;
        foreach (SpaceMemberRecord gone in members.Where(m => m.MemberRole != SpaceRoles.Youth && !staffOfOrg.Contains(m.UserId)))
        {
            gone.LeftAt = now;
            left++;
        }

        List<Guid> adults = [.. members.Where(m => m.LeftAt == null && m.MemberRole != SpaceRoles.Youth && !m.IsMinor).Select(m => m.UserId)];
        HashSet<Guid> eligible = [];
        if (adults.Count >= MinimumStaff)
        {
            foreach (Guid youth in enrolled.Keys)
            {
                if (!(await identity.GetCapabilitiesAsync(new UserId(youth), ct).ConfigureAwait(false)).CanPostInCommunity)
                {
                    continue;
                }

                bool kept = false;
                foreach (Guid adult in adults)
                {
                    kept = kept || await identity.IsContactRestrictedAsync(new UserId(adult), new UserId(youth), ct).ConfigureAwait(false);
                }

                if (!kept)
                {
                    eligible.Add(youth);
                }
            }
        }

        foreach (SpaceMemberRecord gone in members.Where(m => m.LeftAt == null && m.MemberRole == SpaceRoles.Youth && !eligible.Contains(m.UserId)))
        {
            gone.LeftAt = now;
            left++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Two people opening the space at once may both bring the same young person in: the
        // second insert gives way to the first, by the active-member index, instead of failing.
        HashSet<Guid> present = [.. members.Where(m => m.LeftAt == null).Select(m => m.UserId)];
        foreach (Guid youth in eligible.Where(y => !present.Contains(y)))
        {
            bool minor = await identity.IsMinorAsync(new UserId(youth), ct).ConfigureAwait(false);
            joined += await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                insert into community.space_member (id, tenant_id, space_id, user_id, member_role, is_minor, joined_at)
                values ({UuidV7.NewId()}, {space.TenantId}, {space.Id}, {youth}, {SpaceRoles.Youth}, {minor}, {now})
                on conflict (space_id, user_id) where left_at is null do nothing
                """, ct).ConfigureAwait(false);
        }

        if (joined + left > 0)
        {
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = MembershipChanged,
                SubjectKind = SubjectKind,
                SubjectIds = [space.Id],
                ActorOrgId = new OrganisationId(space.OrgId),
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["joined"] = joined.ToString(System.Globalization.CultureInfo.InvariantCulture), ["left"] = left.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            }, ct).ConfigureAwait(false);
        }

        return enrolled;
    }

    // ---- views -------------------------------------------------------------------------------

    private async Task<SpaceSummary> SummaryAsync(SpaceRecord space, UserId viewer, CancellationToken ct)
    {
        List<SpaceMemberRecord> members = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == space.Id && m.LeftAt == null).ToListAsync(ct).ConfigureAwait(false);
        DateTimeOffset? last = await db.Posts.AsNoTracking().Where(p => p.SpaceId == space.Id && p.State == PostStates.Published)
            .MaxAsync(p => p.PublishedAt, ct).ConfigureAwait(false);
        string org = await directory.GetOrganisationNameAsync(new OrganisationId(space.OrgId), ct).ConfigureAwait(false) ?? "";
        return new SpaceSummary(space.Id, space.OrgId, org, space.ListingId, space.Name, space.Description, space.Status,
            members.Count(m => m.MemberRole == SpaceRoles.Youth), members.Count(m => m.MemberRole != SpaceRoles.Youth), last, members.Any(m => m.UserId == viewer.Value));
    }

    private async Task<SpaceView> ViewAsync(SpaceRecord space, UserId viewer, IReadOnlyDictionary<Guid, string?> names, bool forStaff, CancellationToken ct)
    {
        // Every membership the space has had, so a post by someone who has since left still says who they were.
        List<SpaceMemberRecord> everyone = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == space.Id)
            .OrderBy(m => m.JoinedAt).ToListAsync(ct).ConfigureAwait(false);
        Dictionary<Guid, string> roles = [];
        foreach (SpaceMemberRecord m in everyone)
        {
            roles[m.UserId] = m.MemberRole;
        }

        IReadOnlyDictionary<Guid, string> emails = forStaff
            ? await identity.GetAdultEmailsAsync([.. roles.Where(r => r.Value != SpaceRoles.Youth).Select(r => r.Key)], ct).ConfigureAwait(false)
            : new Dictionary<Guid, string>();
        string? NameOf(Guid user) => roles.GetValueOrDefault(user) == SpaceRoles.Youth ? names.GetValueOrDefault(user) : emails.GetValueOrDefault(user);

        List<SpaceMemberRecord> current = [.. everyone.Where(m => m.LeftAt == null)
            .OrderBy(m => m.MemberRole == SpaceRoles.Youth ? 1 : 0).ThenBy(m => NameOf(m.UserId) ?? "￿", StringComparer.CurrentCulture)];
        Guid me = viewer.Value;
        List<PostRecord> top = await db.Posts.AsNoTracking()
            .Where(p => p.SpaceId == space.Id && p.ReplyToId == null
                && (p.State == PostStates.Published || (p.AuthorUserId == me && (p.State == PostStates.Held || p.State == PostStates.Removed))))
            .OrderByDescending(p => p.PublishedAt ?? p.CreatedAt).ThenByDescending(p => p.Id).Take(PageSize).ToListAsync(ct).ConfigureAwait(false);
        List<Guid> shown = [.. top.Where(p => p.State == PostStates.Published).Select(p => p.Id)];
        List<PostRecord> replies = await db.Posts.AsNoTracking()
            .Where(p => p.ReplyToId != null && shown.Contains(p.ReplyToId.Value)
                && (p.State == PostStates.Published || (p.AuthorUserId == me && (p.State == PostStates.Held || p.State == PostStates.Removed))))
            .OrderBy(p => p.PublishedAt ?? p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
        ILookup<Guid, PostRecord> byParent = replies.ToLookup(r => r.ReplyToId!.Value);

        PostView View(PostRecord p, IReadOnlyList<PostView> under) => new(p.Id, p.AuthorUserId == me, roles.GetValueOrDefault(p.AuthorUserId, ""), NameOf(p.AuthorUserId),
            p.Body ?? "", p.State, p.PublishedAt ?? p.CreatedAt, p.State == PostStates.Held && p.AuthorUserId == me ? Moderation.HeldMessages.Reasons(p.ScreenResult) : null, under);

        bool isMember = current.Any(m => m.UserId == me);
        string org = await directory.GetOrganisationNameAsync(new OrganisationId(space.OrgId), ct).ConfigureAwait(false) ?? "";
        return new SpaceView(space.Id, space.OrgId, org, space.ListingId, space.Name, space.Description, space.Status, isMember,
            isMember && space.Status == SpaceStatuses.Active,
            [.. current.Select(m => new SpaceMemberView(m.UserId, m.MemberRole, NameOf(m.UserId), m.JoinedAt, m.UserId == me))],
            [.. top.Select(p => View(p, p.State == PostStates.Published ? [.. byParent[p.Id].Select(r => View(r, []))] : []))]);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Counts a reply in or out of its post, in the one statement, so replies written together all count.</summary>
    internal static Task<int> CountReplyAsync(CommunityDbContext db, Guid parent, int by, CancellationToken ct) =>
        db.Posts.Where(p => p.Id == parent && p.ReplyCount + by >= 0)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ReplyCount, p => p.ReplyCount + by), ct);

    private Task<SpaceMemberRecord?> ActiveMemberAsync(Guid space, UserId user, CancellationToken ct) =>
        db.SpaceMembers.AsNoTracking().FirstOrDefaultAsync(m => m.SpaceId == space && m.UserId == user.Value && m.LeftAt == null, ct);

    private static SpaceMemberRecord Member(SpaceRecord space, Guid user, string role, bool isMinor, DateTimeOffset now) => new()
    {
        Id = UuidV7.NewId(),
        TenantId = space.TenantId,
        SpaceId = space.Id,
        UserId = user,
        MemberRole = role,
        IsMinor = isMinor,
        JoinedAt = now,
    };

    private Task AuditAsync(string action, IReadOnlyCollection<Guid> spaces, CancellationToken ct, OrganisationId? org = null, UserId? person = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = SubjectKind,
            SubjectIds = spaces,
            ActorOrgId = org,
            Detail = person is { } p ? new Dictionary<string, string>(StringComparer.Ordinal) { ["user"] = p.Value.ToString("D") } : null,
        }, ct);

    private static Error NotFound() => Error.NotFound("spaces.not_found", "You aren't in a space with that id.");

    private static Error ArchivedError() => Error.Conflict("spaces.archived", "This space has been archived. It takes no more posts.");

    private static Error AdultsOnly() => Error.Forbidden("spaces.adults_only", "Only an adult member of staff is on a space's staff.");

    internal static Error UnsafeShape() => Error.Conflict("spaces.unsafe_shape",
        "A space with young people in it needs at least two members of staff. Add another before taking this one out.");
}
