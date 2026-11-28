using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>
/// What moderation is about, read the one way whatever it is (D121): a message in a conversation
/// or a post in a space. <see cref="Where"/> is the conversation's subject or the space's name;
/// <see cref="Roles"/> names everyone there by role (participant role or space role), never by name.
/// </summary>
internal sealed record ReportedItem(
    string Kind,
    Guid Id,
    Guid OrgId,
    Guid AuthorUserId,
    string? Where,
    string Body,
    string State,
    DateTimeOffset At,
    string? ScreenResult,
    IReadOnlyDictionary<Guid, string> Roles,
    Guid Container,
    Guid? ReplyToId)
{
    public string AuthorRole => Roles.GetValueOrDefault(AuthorUserId, "");

    public string RoleOf(Guid? user) => user is { } u ? Roles.GetValueOrDefault(u, "") : "";
}

/// <summary>
/// The kinds of thing a report or a moderator's decision can be about, and what each means for
/// finding it, the context shown beside it, and taking it down.
/// </summary>
internal static class ReportTargets
{
    public const string Message = "message";
    public const string Post = "post";

    public static bool Supports(string kind) => kind is Message or Post;

    /// <summary>The message or post, in any state; null when there's none or the kind isn't one moderation reads.</summary>
    public static async Task<ReportedItem?> FindAsync(CommunityDbContext db, string kind, Guid id, CancellationToken ct)
    {
        switch (kind)
        {
            case Message:
                if (await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false) is not { } message)
                {
                    return null;
                }

                ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
                Dictionary<Guid, string> participants = await db.Participants.AsNoTracking()
                    .Where(p => p.ConversationId == conversation.Id)
                    .ToDictionaryAsync(p => p.UserId, p => p.ParticipantRole, ct).ConfigureAwait(false);
                return new ReportedItem(Message, message.Id, conversation.OrgId, message.SenderUserId, conversation.Subject, message.Body, message.State,
                    message.SentAt, message.ScreenResult, participants, conversation.Id, null);

            case Post:
                if (await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false) is not { } post)
                {
                    return null;
                }

                SpaceRecord space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == post.SpaceId, ct).ConfigureAwait(false);
                return new ReportedItem(Post, post.Id, space.OrgId, post.AuthorUserId, space.Name, post.Body ?? "", post.State,
                    post.PublishedAt ?? post.CreatedAt, post.ScreenResult, await SpaceRolesAsync(db, space.Id, ct).ConfigureAwait(false), space.Id, post.ReplyToId);

            default:
                return null;
        }
    }

    /// <summary>Whether it's where its readers see it: a delivered message, a published post.</summary>
    public static bool IsUp(ReportedItem item) =>
        item.State == (item.Kind == Message ? MessageStates.Sent : PostStates.Published);

    /// <summary>
    /// What a moderator reads before it: the few messages before it in the conversation, or the
    /// post a reply answers.
    /// </summary>
    public static async Task<IReadOnlyList<EarlierMessage>> EarlierAsync(CommunityDbContext db, ReportedItem item, CancellationToken ct)
    {
        if (item.Kind == Message)
        {
            List<MessageRecord> earlier = await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == item.Container && m.State == MessageStates.Sent && m.SentAt < item.At)
                .OrderByDescending(m => m.SentAt).Take(HeldMessages.EarlierCount).ToListAsync(ct).ConfigureAwait(false);
            earlier.Reverse();
            return [.. earlier.Select(m => new EarlierMessage(item.RoleOf(m.SenderUserId), m.Body, m.SentAt))];
        }

        if (item.ReplyToId is { } parentId && await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentId, ct).ConfigureAwait(false) is { } parent)
        {
            return [new EarlierMessage(item.RoleOf(parent.AuthorUserId), parent.Body ?? "", parent.PublishedAt ?? parent.CreatedAt)];
        }

        return [];
    }

    /// <summary>
    /// Takes it down, if it's still up: a message is marked removed; a post too, and a reply stops
    /// counting under its post. A post's own replies stay where they are (D121).
    /// </summary>
    public static async Task<bool> TakeDownAsync(CommunityDbContext db, ReportedItem item, Guid moderator, DateTimeOffset now, CancellationToken ct)
    {
        if (item.Kind == Message)
        {
            return await db.Messages.Where(m => m.Id == item.Id && m.State == MessageStates.Sent)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.State, MessageStates.Removed)
                    .SetProperty(m => m.RemovedAt, now).SetProperty(m => m.RemovedBy, moderator), ct).ConfigureAwait(false) > 0;
        }

        bool down = await db.Posts.Where(p => p.Id == item.Id && p.State == PostStates.Published)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PostStates.Removed).SetProperty(p => p.PublishedAt, (DateTimeOffset?)null)
                .SetProperty(p => p.RemovedAt, now).SetProperty(p => p.RemovedBy, moderator), ct).ConfigureAwait(false) > 0;
        if (down && item.ReplyToId is { } parent)
        {
            await Groups.Spaces.CountReplyAsync(db, parent, -1, ct).ConfigureAwait(false);
        }

        return down;
    }

    /// <summary>Everyone who has been in a space, by the role they last had there.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> SpaceRolesAsync(CommunityDbContext db, Guid space, CancellationToken ct)
    {
        Dictionary<Guid, string> roles = [];
        foreach (SpaceMemberRecord m in await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == space).OrderBy(m => m.JoinedAt).ToListAsync(ct).ConfigureAwait(false))
        {
            roles[m.UserId] = m.MemberRole;
        }

        return roles;
    }
}
