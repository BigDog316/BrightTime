using System.Text.Json;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>A message the conversation held before the one held, as the moderator reads it: by role, never by name.</summary>
[YouthData]
public sealed record EarlierMessage(string SenderRole, string Body, DateTimeOffset SentAt);

/// <summary>
/// A message screening held (D53), as a county moderator sees it (D114): who wrote it by role, the
/// organisation, why it was held, and the few messages before it. <see cref="OwnOrganisation"/>
/// is set when the moderator is the sender or a member of the sender's organisation, who may not
/// decide it.
/// </summary>
[YouthData]
public sealed record HeldMessage(
    Guid Id,
    Guid ConversationId,
    Guid OrgId,
    string OrgName,
    string? Subject,
    string SenderRole,
    string Body,
    IReadOnlyList<string> Reasons,
    DateTimeOffset SentAt,
    bool OwnOrganisation,
    IReadOnlyList<EarlierMessage> Earlier);

/// <summary>
/// The county's queue of messages screening held (D114): an adult's message into a conversation
/// with a young person that looked like an attempt to take it off the platform. A moderator
/// releases one, and it is delivered as if sent then, or removes it, and nobody but its sender
/// sees it, marked removed. Either needs a reason, and each is a row of the append-only
/// <c>moderation_action</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nobody of the sender's organisation decides: the moderator who is the sender, or a member of
/// that organisation, is refused (<c>moderation.own_organisation</c>). Who may use the queue at
/// all is the page's permission (<c>moderation.triage</c>, <c>moderation.remove</c>).
/// </para>
/// <para>
/// A message is decided once: the change is conditional on its still being held, so two
/// moderators deciding together get one decision and one refusal. A release is refused when the
/// conversation has been closed, or when a contact restriction now keeps the sender from a young
/// person in it; removing it is always possible.
/// </para>
/// </remarks>
public sealed class HeldMessages(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    Conversations conversations)
{
    public const string Read = "moderation.held_read";
    public const string Released = "moderation.message_released";
    public const string Removed = "moderation.message_removed";

    public const int MaxReason = 1000;

    /// <summary>How many messages before a held one the moderator reads with it.</summary>
    public const int EarlierCount = 3;

    /// <summary>The most the queue shows at once, oldest first.</summary>
    public const int PageSize = 100;

    private const string TargetKind = "message";

    /// <summary>The messages waiting for a moderator, oldest first; reading them is audited.</summary>
    public Task<IReadOnlyList<HeldMessage>> ListAsync(UserId moderator, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<HeldMessage>>(async ct =>
        {
            List<MessageRecord> held = await db.Messages.AsNoTracking()
                .Where(m => m.State == MessageStates.Held)
                .OrderBy(m => m.SentAt).ThenBy(m => m.Id)
                .Take(PageSize).ToListAsync(ct).ConfigureAwait(false);
            List<HeldMessage> list = [];
            Dictionary<Guid, (string Name, bool Mine)> orgs = [];
            foreach (MessageRecord message in held)
            {
                ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
                if (!orgs.TryGetValue(conversation.OrgId, out (string Name, bool Mine) org))
                {
                    OrganisationId id = new(conversation.OrgId);
                    org = (await directory.GetOrganisationNameAsync(id, ct).ConfigureAwait(false) ?? "", await MemberAsync(id, moderator, ct).ConfigureAwait(false));
                    orgs[conversation.OrgId] = org;
                }

                Dictionary<Guid, string> roles = await db.Participants.AsNoTracking()
                    .Where(p => p.ConversationId == conversation.Id)
                    .ToDictionaryAsync(p => p.UserId, p => p.ParticipantRole, ct).ConfigureAwait(false);
                List<MessageRecord> earlier = await db.Messages.AsNoTracking()
                    .Where(m => m.ConversationId == conversation.Id && m.State == MessageStates.Sent && m.SentAt < message.SentAt)
                    .OrderByDescending(m => m.SentAt).Take(EarlierCount).ToListAsync(ct).ConfigureAwait(false);
                earlier.Reverse();

                list.Add(new HeldMessage(
                    message.Id,
                    conversation.Id,
                    conversation.OrgId,
                    org.Name,
                    conversation.Subject,
                    roles.GetValueOrDefault(message.SenderUserId, ""),
                    message.Body,
                    Reasons(message.ScreenResult),
                    message.SentAt,
                    org.Mine || message.SenderUserId == moderator.Value,
                    [.. earlier.Select(m => new EarlierMessage(roles.GetValueOrDefault(m.SenderUserId, ""), m.Body, m.SentAt))]));
            }

            await AuditAsync(Read, [.. list.Select(m => m.Id)], ct).ConfigureAwait(false);
            return list;
        }, cancellationToken);

    /// <summary>Delivers a held message, as if sent now, and tells the conversation's other participants.</summary>
    public Task<Result> ReleaseAsync(UserId moderator, Guid messageId, string? reason, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, messageId, reason, release: true, cancellationToken);

    /// <summary>Keeps a held message from everyone but its sender, who sees it marked removed.</summary>
    public Task<Result> RemoveAsync(UserId moderator, Guid messageId, string? reason, CancellationToken cancellationToken = default) =>
        DecideAsync(moderator, messageId, reason, release: false, cancellationToken);

    private Task<Result> DecideAsync(UserId moderator, Guid messageId, string? reason, bool release, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            if (reason?.Trim() is not { Length: > 0 and <= MaxReason } why)
            {
                return Result.Failure(Error.Validation("moderation.reason", $"Give a reason of up to {MaxReason} characters; it's kept in the moderation record."));
            }

            if (await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.State == MessageStates.Held, ct).ConfigureAwait(false) is not { } message)
            {
                return Result.Failure(NotHeld());
            }

            ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, ct).ConfigureAwait(false);
            if (message.SenderUserId == moderator.Value || await MemberAsync(new OrganisationId(conversation.OrgId), moderator, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Forbidden("moderation.own_organisation",
                    "You're a member of the organisation that wrote this message, so another moderator decides it."));
            }

            if (release)
            {
                if (conversation.ClosedAt is not null)
                {
                    return Result.Failure(Error.Conflict("moderation.closed", "This conversation has been closed, so the message can't be delivered. You can remove it."));
                }

                List<Guid> minors = await db.Participants.AsNoTracking()
                    .Where(p => p.ConversationId == conversation.Id && p.IsMinor && p.RemovedAt == null)
                    .Select(p => p.UserId).ToListAsync(ct).ConfigureAwait(false);
                foreach (Guid minor in minors)
                {
                    if (await identity.IsContactRestrictedAsync(new UserId(message.SenderUserId), new UserId(minor), ct).ConfigureAwait(false))
                    {
                        return Result.Failure(Error.Conflict("moderation.restricted",
                            "A contact restriction now keeps the sender from a young person in this conversation, so the message can't be delivered. You can remove it."));
                    }
                }
            }

            DateTimeOffset now = clock.StoredNow();
            int changed = release
                ? await db.Messages.Where(m => m.Id == messageId && m.State == MessageStates.Held)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.State, MessageStates.Sent).SetProperty(m => m.SentAt, now), ct).ConfigureAwait(false)
                : await db.Messages.Where(m => m.Id == messageId && m.State == MessageStates.Held)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.State, MessageStates.Removed)
                        .SetProperty(m => m.RemovedAt, now).SetProperty(m => m.RemovedBy, moderator.Value), ct).ConfigureAwait(false);
            if (changed == 0)
            {
                return Result.Failure(NotHeld());
            }

            db.ModerationActions.Add(new ModerationActionRecord
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                TargetKind = TargetKind,
                TargetId = messageId,
                Action = release ? "publish" : "remove",
                ActorUserId = moderator.Value,
                Reason = why,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (release)
            {
                message.State = MessageStates.Sent;
                message.SentAt = now;
                await conversations.NotifyAsync(conversation, message, ct).ConfigureAwait(false);
            }

            await AuditAsync(release ? Released : Removed, [messageId], ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The screen's reasons, from the message's <c>screen_result</c> (<c>{"reasons": [...]}</c>).</summary>
    internal static IReadOnlyList<string> Reasons(string? screenResult)
    {
        if (string.IsNullOrEmpty(screenResult))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(screenResult);
        return document.RootElement.TryGetProperty("reasons", out JsonElement reasons) && reasons.ValueKind == JsonValueKind.Array
            ? [.. reasons.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0)]
            : [];
    }

    private async Task<bool> MemberAsync(OrganisationId org, UserId person, CancellationToken ct) =>
        (await directory.GetOrganisationMembersAsync(org, ct).ConfigureAwait(false)).Any(m => m.UserId == person);

    private Task AuditAsync(string action, IReadOnlyCollection<Guid> messages, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = TargetKind,
            SubjectIds = messages,
        }, ct);

    private static Error NotHeld() => Error.NotFound("moderation.not_held", "That message isn't waiting for a moderator any more.");
}
