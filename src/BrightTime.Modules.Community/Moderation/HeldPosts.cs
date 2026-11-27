using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>
/// A post screening held (D120), as a county moderator sees it: the organisation and space, who
/// wrote it by role, why it was held, and the post it answers, if it's a reply.
/// <see cref="OwnOrganisation"/> is set when the moderator wrote it or is of the space's
/// organisation, and may not decide it.
/// </summary>
[YouthData]
public sealed record HeldPost(
    Guid Id,
    Guid SpaceId,
    Guid OrgId,
    string OrgName,
    string SpaceName,
    string AuthorRole,
    string Body,
    IReadOnlyList<string> Reasons,
    DateTimeOffset WrittenAt,
    bool OwnOrganisation,
    EarlierMessage? Answering);

/// <summary>
/// The posts screening held (D120), beside the messages (D114): what a young person wrote that
/// carried personal information, or an adult's post where young people read that looked like an
/// attempt to take them off the platform. A moderator releases one, and it's published as of then,
/// or removes it, and only its author sees it, marked removed; either with a reason, as a row of
/// <c>moderation_action</c> on the post.
/// </summary>
/// <remarks>
/// Nobody of the space's organisation decides, and nobody decides their own. A post is decided
/// once. A release is refused when the space has been archived, when its author is no longer in
/// it, or when the post it answers is no longer published; removing it is always possible.
/// </remarks>
public sealed class HeldPosts(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IDirectoryQueries directory)
{
    public const string Read = "moderation.held_posts_read";
    public const string Released = "moderation.post_released";
    public const string Removed = "moderation.post_removed";

    private const string TargetKind = "post";

    /// <summary>The posts waiting for a moderator, oldest first; reading them is audited.</summary>
    public Task<IReadOnlyList<HeldPost>> ListAsync(UserId moderator, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<HeldPost>>(async ct =>
        {
            List<PostRecord> held = await db.Posts.AsNoTracking().Where(p => p.State == PostStates.Held)
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).Take(HeldMessages.PageSize).ToListAsync(ct).ConfigureAwait(false);
            List<HeldPost> list = [];
            Dictionary<Guid, (string Name, bool Mine)> orgs = [];
            foreach (PostRecord post in held)
            {
                SpaceRecord space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == post.SpaceId, ct).ConfigureAwait(false);
                if (!orgs.TryGetValue(space.OrgId, out (string Name, bool Mine) org))
                {
                    OrganisationId id = new(space.OrgId);
                    org = (await directory.GetOrganisationNameAsync(id, ct).ConfigureAwait(false) ?? "", await MemberAsync(id, moderator, ct).ConfigureAwait(false));
                    orgs[space.OrgId] = org;
                }

                EarlierMessage? answering = null;
                if (post.ReplyToId is { } parentId && await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentId, ct).ConfigureAwait(false) is { } parent)
                {
                    answering = new EarlierMessage(await RoleAsync(space.Id, parent.AuthorUserId, ct).ConfigureAwait(false), parent.Body ?? "", parent.PublishedAt ?? parent.CreatedAt);
                }

                list.Add(new HeldPost(post.Id, space.Id, space.OrgId, org.Name, space.Name, await RoleAsync(space.Id, post.AuthorUserId, ct).ConfigureAwait(false),
                    post.Body ?? "", HeldMessages.Reasons(post.ScreenResult), post.CreatedAt, org.Mine || post.AuthorUserId == moderator.Value, answering));
            }

            await AuditAsync(Read, [.. list.Select(p => p.Id)], ct).ConfigureAwait(false);
            return list;
        }, cancellationToken);

    /// <summary>Publishes a held post, as of now.</summary>
    public Task<Result> ReleaseAsync(UserId moderator, Guid postId, string? reason, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, postId, reason, release: true, cancellationToken);

    /// <summary>Keeps a held post from everyone but its author, who sees it marked removed.</summary>
    public Task<Result> RemoveAsync(UserId moderator, Guid postId, string? reason, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, postId, reason, release: false, cancellationToken);

    private Task<Result> DecideAsync(UserId moderator, Guid postId, string? reason, bool release, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (reason?.Trim() is not { Length: > 0 and <= HeldMessages.MaxReason } why)
            {
                return Result.Failure(Error.Validation("moderation.reason", $"Give a reason of up to {HeldMessages.MaxReason} characters; it's kept in the moderation record."));
            }

            if (await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId && p.State == PostStates.Held, ct).ConfigureAwait(false) is not { } post)
            {
                return Result.Failure(NotHeld());
            }

            SpaceRecord space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == post.SpaceId, ct).ConfigureAwait(false);
            if (post.AuthorUserId == moderator.Value || await MemberAsync(new OrganisationId(space.OrgId), moderator, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Forbidden("moderation.own_space",
                    "You wrote this post, or you're a member of the organisation whose space it's in, so another moderator decides it."));
            }

            PostRecord? parent = null;
            if (release)
            {
                if (space.Status != SpaceStatuses.Active)
                {
                    return Result.Failure(Error.Conflict("moderation.space_archived", "This space has been archived, so the post can't be published. You can remove it."));
                }

                if (!await db.SpaceMembers.AnyAsync(m => m.SpaceId == space.Id && m.UserId == post.AuthorUserId && m.LeftAt == null, ct).ConfigureAwait(false))
                {
                    return Result.Failure(Error.Conflict("moderation.author_left", "Its author is no longer in this space, so the post can't be published. You can remove it."));
                }

                if (post.ReplyToId is { } parentId)
                {
                    parent = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentId && p.State == PostStates.Published, ct).ConfigureAwait(false);
                    if (parent is null)
                    {
                        return Result.Failure(Error.Conflict("moderation.parent_gone", "The post this answers is no longer published, so the reply can't be. You can remove it."));
                    }
                }
            }

            DateTimeOffset now = clock.StoredNow();
            int changed = release
                ? await db.Posts.Where(p => p.Id == postId && p.State == PostStates.Held)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PostStates.Published).SetProperty(p => p.PublishedAt, now), ct).ConfigureAwait(false)
                : await db.Posts.Where(p => p.Id == postId && p.State == PostStates.Held)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PostStates.Removed)
                        .SetProperty(p => p.RemovedAt, now).SetProperty(p => p.RemovedBy, moderator.Value), ct).ConfigureAwait(false);
            if (changed == 0)
            {
                return Result.Failure(NotHeld());
            }

            if (parent is not null)
            {
                await Groups.Spaces.CountReplyAsync(db, parent.Id, +1, ct).ConfigureAwait(false);
            }

            db.ModerationActions.Add(new ModerationActionRecord
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                TargetKind = TargetKind,
                TargetId = postId,
                Action = release ? "publish" : "remove",
                ActorUserId = moderator.Value,
                Reason = why,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(release ? Released : Removed, [postId], ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    private async Task<string> RoleAsync(Guid space, Guid user, CancellationToken ct) =>
        await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == space && m.UserId == user)
            .OrderByDescending(m => m.JoinedAt).Select(m => m.MemberRole).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? "";

    private async Task<bool> MemberAsync(OrganisationId org, UserId person, CancellationToken ct) =>
        (await directory.GetOrganisationMembersAsync(org, ct).ConfigureAwait(false)).Any(m => m.UserId == person);

    private Task AuditAsync(string action, IReadOnlyCollection<Guid> posts, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = TargetKind,
            SubjectIds = posts,
        }, ct);

    private static Error NotHeld() => Error.NotFound("moderation.post_not_held", "That post isn't waiting for a moderator any more.");
}
