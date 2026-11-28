using System.Text;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Groups;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Learning.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>
/// What moderation is about, read the one way whatever it is (D121, D128): a message, a post, an
/// item on the showcase, a piece of challenge work, or a shown portfolio. <see cref="Where"/> is
/// the conversation's subject, the space's name, the item's or challenge's title, or the
/// portfolio's name; <see cref="Roles"/> names everyone there by role, never by name. A portfolio
/// is a young person's own, of no organisation (<see cref="OrgId"/> empty).
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

    /// <summary>The reporter by role: someone not signed in, someone there, or a signed-in visitor to a public page.</summary>
    public string ReporterRole(Guid? reporter) =>
        reporter is not { } u ? ReportTargets.PublicReporter : Roles.TryGetValue(u, out string? role) ? role : ReportTargets.Visitor;
}

/// <summary>
/// The kinds of thing a report or a moderator's decision can be about, and what each means for
/// finding it, the context shown beside it, and taking it down.
/// </summary>
internal sealed class ReportTargets(CommunityDbContext db, ITenantContext tenant, IPortfolioModeration? portfolios = null)
{
    public const string Message = "message";
    public const string Post = "post";

    /// <summary>An item on the public showcase (D124, D128).</summary>
    public const string ShowcaseItem = "showcase_item";

    /// <summary>A young person's work for a challenge, reported by the space's staff (D128).</summary>
    public const string Submission = "submission";

    /// <summary>A shown portfolio, with its projects (D128).</summary>
    public const string Portfolio = "portfolio";

    /// <summary>The reporter's role when nobody was signed in.</summary>
    public const string PublicReporter = "public";

    /// <summary>The reporter's role when someone signed in reported a public page.</summary>
    public const string Visitor = "visitor";

    /// <summary>What a reported item's author is, where there's no membership to say: a young person.</summary>
    public const string YoungPerson = "youth";

    /// <summary>The kinds anyone may report, signed in or not, from a public page.</summary>
    public static bool IsPublic(string kind) => kind is ShowcaseItem or Portfolio;

    /// <summary>The item, in any state; null when there's none or the kind isn't one moderation reads.</summary>
    public async Task<ReportedItem?> FindAsync(string kind, Guid id, CancellationToken ct)
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

            case ShowcaseItem:
                if (await db.ShowcaseItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false) is not { } item)
                {
                    return null;
                }

                string shown = item.Caption is { Length: > 0 } caption ? item.Body + "\n\n" + caption : item.Body;
                return new ReportedItem(ShowcaseItem, item.Id, item.OrgId, item.AuthorUserId, item.Title, shown,
                    item.UnpublishedAt is not null ? "taken_down" : item.PublishedAt is not null ? "shown" : "waiting",
                    item.PublishedAt ?? item.ApprovedAt, null, new Dictionary<Guid, string> { [item.AuthorUserId] = YoungPerson }, item.Id, null);

            case Submission:
                var work = await (from s in db.Submissions.AsNoTracking()
                                  join c in db.Challenges on s.ChallengeId equals c.Id
                                  join sp in db.Spaces on c.SpaceId equals sp.Id
                                  where s.Id == id
                                  select new { s, c.Title, sp.OrgId, SpaceId = sp.Id }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return work is null
                    ? null
                    : new ReportedItem(Submission, work.s.Id, work.OrgId, work.s.AuthorUserId, work.Title, work.s.Body ?? "", work.s.State,
                        work.s.SubmittedAt ?? work.s.UpdatedAt, work.s.ScreenResult, await SpaceRolesAsync(db, work.SpaceId, ct).ConfigureAwait(false),
                        work.s.ChallengeId, null);

            case Portfolio:
                if (portfolios is null || await portfolios.OfAsync(id, ct).ConfigureAwait(false) is not { } page)
                {
                    return null;
                }

                return new ReportedItem(Portfolio, page.UserId, Guid.Empty, page.UserId, page.DisplayName, Text(page), page.Visibility,
                    page.UpdatedAt, null, new Dictionary<Guid, string> { [page.UserId] = YoungPerson }, page.UserId, null);

            default:
                return null;
        }
    }

    /// <summary>Whether it's where its readers see it: a delivered message, a published post, a shown item or portfolio, work that was sent.</summary>
    public static bool IsUp(ReportedItem item) => item.Kind switch
    {
        Message => item.State == MessageStates.Sent,
        Post => item.State == PostStates.Published,
        ShowcaseItem => item.State == "shown",
        Submission => item.State is not (SubmissionStates.Draft or SubmissionStates.Removed),
        Portfolio => item.State is "link_only" or "public",
        _ => false,
    };

    /// <summary>
    /// What a moderator reads before it: the few messages before it in the conversation, the post a
    /// reply answers, or the challenge's brief a piece of work answers.
    /// </summary>
    public async Task<IReadOnlyList<EarlierMessage>> EarlierAsync(ReportedItem item, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case Message:
                List<MessageRecord> earlier = await db.Messages.AsNoTracking()
                    .Where(m => m.ConversationId == item.Container && m.State == MessageStates.Sent && m.SentAt < item.At)
                    .OrderByDescending(m => m.SentAt).Take(HeldMessages.EarlierCount).ToListAsync(ct).ConfigureAwait(false);
                earlier.Reverse();
                return [.. earlier.Select(m => new EarlierMessage(item.RoleOf(m.SenderUserId), m.Body, m.SentAt))];

            case Post when item.ReplyToId is { } parentId:
                return await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentId, ct).ConfigureAwait(false) is { } parent
                    ? [new EarlierMessage(item.RoleOf(parent.AuthorUserId), parent.Body ?? "", parent.PublishedAt ?? parent.CreatedAt)]
                    : [];

            case Submission:
                return await db.Challenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == item.Container, ct).ConfigureAwait(false) is { } challenge
                    ? [new EarlierMessage(item.RoleOf(challenge.CreatedBy), challenge.BriefMd, challenge.CreatedAt)]
                    : [];

            default:
                return [];
        }
    }

    /// <summary>
    /// Takes it down, if it's still up: a message or post is marked removed (a reply stops counting
    /// under its post; a post's own replies stay, D121); a showcase item comes off the showcase; work
    /// is removed, so it's on no showcase either; a portfolio goes private, by Learning, on the worker.
    /// </summary>
    public async Task<bool> TakeDownAsync(ReportedItem item, Guid moderator, Guid reportId, DateTimeOffset now, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case Message:
                return await db.Messages.Where(m => m.Id == item.Id && m.State == MessageStates.Sent)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.State, MessageStates.Removed)
                        .SetProperty(m => m.RemovedAt, now).SetProperty(m => m.RemovedBy, moderator), ct).ConfigureAwait(false) > 0;

            case Post:
                bool down = await db.Posts.Where(p => p.Id == item.Id && p.State == PostStates.Published)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PostStates.Removed).SetProperty(p => p.PublishedAt, (DateTimeOffset?)null)
                        .SetProperty(p => p.RemovedAt, now).SetProperty(p => p.RemovedBy, moderator), ct).ConfigureAwait(false) > 0;
                if (down && item.ReplyToId is { } parent)
                {
                    await Spaces.CountReplyAsync(db, parent, -1, ct).ConfigureAwait(false);
                }

                return down;

            case ShowcaseItem:
                return await db.ShowcaseItems.Where(i => i.Id == item.Id && i.UnpublishedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.UnpublishedAt, now).SetProperty(i => i.UnpublishedBy, moderator), ct).ConfigureAwait(false) > 0;

            case Submission:
                return await db.Submissions.Where(w => w.Id == item.Id && w.State != SubmissionStates.Draft && w.State != SubmissionStates.Removed)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.State, SubmissionStates.Removed).SetProperty(w => w.UpdatedAt, now), ct).ConfigureAwait(false) > 0;

            case Portfolio when IsUp(item):
                await db.PublishAsync(tenant.TenantId, new PortfolioTakenDown(item.AuthorUserId, reportId), ct).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
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

    /// <summary>A portfolio as a moderator reads it: its name, headline, bio, and each shown project.</summary>
    private static string Text(ReportablePortfolio page)
    {
        StringBuilder text = new(page.DisplayName);
        foreach (string? line in new[] { page.Headline, page.Bio })
        {
            if (line is { Length: > 0 })
            {
                text.Append("\n\n").Append(line);
            }
        }

        foreach (ReportableProject project in page.Projects)
        {
            text.Append("\n\n").Append(project.Title);
            if (project.Description is { Length: > 0 } description)
            {
                text.Append(": ").Append(description);
            }
        }

        return text.ToString();
    }
}
