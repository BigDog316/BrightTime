using System.Text.Json;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Community.Messaging;

[YouthData]
public sealed record ConversationSummary(Guid Id, Guid OrgId, string Kind, string? Subject, bool IsOrgVisible, bool IncludesMinor, DateTimeOffset? LastMessageAt, int Unread, DateTimeOffset? ClosedAt);

/// <summary>A message as one participant sees it. A held message is shown only to its sender, marked so.</summary>
[YouthData]
public sealed record MessageView(Guid Id, bool FromMe, string SenderRole, string Body, string State, DateTimeOffset SentAt);

/// <summary>
/// A thread as a participant sees it. <see cref="ReadByOrganisation"/> is what the screen tells
/// everyone: when a young person is in it, the organisation's admin reads it too (T1).
/// </summary>
[YouthData]
public sealed record ConversationView(Guid Id, Guid OrgId, string Kind, string? Subject, bool ReadByOrganisation, IReadOnlyList<string> Roles, IReadOnlyList<MessageView> Messages);

/// <summary>An announcement as sent: the conversation, how many families it reached, and how many young people it had to leave out.</summary>
[YouthData]
public sealed record BroadcastResult(ConversationView Conversation, int Recipients, int Skipped);

/// <summary>
/// Conversations between an organisation and a family (M3b, D53), with the safety rules the schema
/// enforces (docs/plan/07, T1) made the application's own first step.
/// </summary>
/// <remarks>
/// <para>
/// There is no pair. A conversation that includes a minor carries at least two adults and is
/// visible to the organisation; this class adds an organisation admin as an observer before
/// asking, and refuses (<c>messaging.unsafe_shape</c>) when there is no second adult to add. The
/// database refuses anyway: <c>enforce_safe_conversation()</c> checks every participant change at
/// commit, which is the guarantee; this is the courtesy.
/// </para>
/// <para>
/// A young person is messaged only if their consent allows it (<c>can_be_messaged_by_org</c>), and
/// never by someone a contact restriction keeps away. An adult's message into a conversation with
/// a minor is screened for an attempt to move it off the platform, and held for a moderator if it
/// looks like one.
/// </para>
/// <para>
/// A push says only that there is a new message, and from which organisation; never what it says
/// (T13). Every read and send is audited, with the organisation as the actor's when staff act.
/// </para>
/// </remarks>
public sealed class Conversations(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IEnrolmentQueries enrolments,
    IListingDetails listings)
{
    public const string Read = "messaging.read";
    public const string Sent = "messaging.sent";

    public const int MaxBody = 8000;
    public const int MaxSubject = 200;

    public const string NewMessageKind = "new_message";
    public const string NewMessageTitle = "notify.message.new";

    /// <summary>
    /// A member of an organisation's staff opens a conversation with the family behind one of its
    /// enrolments or submitted applications, with a first message.
    /// </summary>
    public Task<Result<ConversationView>> StartAsync(OrganisationId org, UserId staff, Guid? enrollmentId, Guid? applicationId, string? subject, string body, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? text = Body(body);
            if (text is null)
            {
                return Result<ConversationView>.Failure(BadBody());
            }

            string? topic = subject?.Trim() is { Length: > 0 } s ? s : null;
            if (topic is { Length: > MaxSubject })
            {
                return Result<ConversationView>.Failure(Error.Validation("messaging.subject", $"A subject of at most {MaxSubject} characters."));
            }

            if (await enrolments.FamilyOfAsync(enrollmentId, applicationId, ct).ConfigureAwait(false) is not { } family || family.OrgId != org.Value)
            {
                return Result<ConversationView>.Failure(Error.NotFound("messaging.family_not_found", "Your organisation has no enrolment or submitted application with that id."));
            }

            UserId owner = new(family.OwnerUserId);
            if (owner == staff)
            {
                return Result<ConversationView>.Failure(Error.Validation("messaging.self", "You can't open a conversation with yourself."));
            }

            bool ownerIsMinor = await identity.IsMinorAsync(owner, ct).ConfigureAwait(false);
            if (await identity.IsMinorAsync(staff, ct).ConfigureAwait(false))
            {
                return Result<ConversationView>.Failure(Error.Forbidden("messaging.forbidden", "Only an adult member of staff opens a conversation with a family."));
            }

            if (ownerIsMinor && !(await identity.GetCapabilitiesAsync(owner, ct).ConfigureAwait(false)).CanBeMessagedByOrganisation)
            {
                return Result<ConversationView>.Failure(Error.Forbidden("messaging.not_messageable", "This young person's consent doesn't allow messages from organisations yet."));
            }

            if (await identity.IsContactRestrictedAsync(staff, owner, ct).ConfigureAwait(false))
            {
                return Result<ConversationView>.Failure(Error.Forbidden("messaging.restricted", "A contact restriction keeps you from messaging this family."));
            }

            // A minor is never in a conversation with one adult: an admin of the organisation reads it too.
            Guid? observer = null;
            if (ownerIsMinor)
            {
                observer = await ObserverAsync(org, [staff.Value, owner.Value], [owner], ct).ConfigureAwait(false);
                if (observer is null)
                {
                    return Result<ConversationView>.Failure(Error.Conflict("messaging.unsafe_shape",
                        "A conversation with a young person needs a second adult from your organisation. Ask your programme coordinator to join."));
                }
            }

            DateTimeOffset now = clock.StoredNow();
            ConversationRecord conversation = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                ListingId = family.ListingId,
                ConversationKind = ConversationKinds.OrgToFamily,
                Subject = topic,
                IsOrgVisible = true,
                CreatedBy = staff.Value,
                CreatedAt = now,
            };
            db.Conversations.Add(conversation);
            db.Participants.Add(Participant(conversation.Id, staff.Value, ParticipantRoles.Staff, isMinor: false, now));
            db.Participants.Add(Participant(conversation.Id, owner.Value, ownerIsMinor ? ParticipantRoles.Youth : ParticipantRoles.Guardian, ownerIsMinor, now));
            if (observer is { } reader)
            {
                db.Participants.Add(Participant(conversation.Id, reader, ParticipantRoles.OrgAdminObserver, isMinor: false, now, implicitReader: true));
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await WriteAsync(conversation, staff, text, ownerIsMinor, ct).ConfigureAwait(false);
            await AuditAsync(Sent, conversation, [conversation.Id], ct, org).ConfigureAwait(false);
            return Result<ConversationView>.Success(await ViewAsync(conversation, staff, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// An announcement from staff to every family with a young person enrolled in a listing (D54):
    /// one <c>org_broadcast</c> conversation that families read and don't answer, and that names no
    /// family to another. With any young person among the recipients an admin of the organisation
    /// observes it; without one, young people are left out (and counted) rather than sent to
    /// without a second adult of the organisation's. So are young people whose consent doesn't
    /// allow it, and anyone a contact restriction keeps from the sender.
    /// </summary>
    public Task<Result<BroadcastResult>> BroadcastAsync(OrganisationId org, UserId staff, Guid listingId, string? subject, string body, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (Body(body) is not { } text)
            {
                return Result<BroadcastResult>.Failure(BadBody());
            }

            string? topic = subject?.Trim() is { Length: > 0 } s ? s : null;
            if (topic is { Length: > MaxSubject })
            {
                return Result<BroadcastResult>.Failure(Error.Validation("messaging.subject", $"A subject of at most {MaxSubject} characters."));
            }

            if ((await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId != org)
            {
                return Result<BroadcastResult>.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            if (await identity.IsMinorAsync(staff, ct).ConfigureAwait(false))
            {
                return Result<BroadcastResult>.Failure(Error.Forbidden("messaging.forbidden", "Only an adult member of staff sends announcements."));
            }

            List<(Guid User, bool Minor)> recipients = [];
            int skipped = 0;
            foreach (Guid owner in (await enrolments.FamilyOwnersAsync(listingId, ct).ConfigureAwait(false)).Where(o => o != staff.Value).Distinct())
            {
                UserId who = new(owner);
                bool minor = await identity.IsMinorAsync(who, ct).ConfigureAwait(false);
                if (await identity.IsContactRestrictedAsync(staff, who, ct).ConfigureAwait(false)
                    || (minor && !(await identity.GetCapabilitiesAsync(who, ct).ConfigureAwait(false)).CanBeMessagedByOrganisation))
                {
                    skipped++;
                    continue;
                }

                recipients.Add((owner, minor));
            }

            Guid? observer = null;
            if (recipients.Any(r => r.Minor))
            {
                List<UserId> minors = [.. recipients.Where(r => r.Minor).Select(r => new UserId(r.User))];
                observer = await ObserverAsync(org, [staff.Value, .. recipients.Select(r => r.User)], minors, ct).ConfigureAwait(false);
                if (observer is null)
                {
                    skipped += minors.Count;
                    recipients.RemoveAll(r => r.Minor);
                }
            }

            if (recipients.Count == 0)
            {
                return Result<BroadcastResult>.Failure(Error.Conflict("messaging.no_recipients", "No family of this listing can be sent this announcement."));
            }

            DateTimeOffset now = clock.StoredNow();
            ConversationRecord conversation = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                ListingId = listingId,
                ConversationKind = ConversationKinds.OrgBroadcast,
                Subject = topic,
                IsOrgVisible = true,
                CreatedBy = staff.Value,
                CreatedAt = now,
            };
            db.Conversations.Add(conversation);
            db.Participants.Add(Participant(conversation.Id, staff.Value, ParticipantRoles.Staff, isMinor: false, now));
            foreach ((Guid user, bool minor) in recipients)
            {
                db.Participants.Add(Participant(conversation.Id, user, minor ? ParticipantRoles.Youth : ParticipantRoles.Guardian, minor, now));
            }

            if (observer is { } reader)
            {
                db.Participants.Add(Participant(conversation.Id, reader, ParticipantRoles.OrgAdminObserver, isMinor: false, now, implicitReader: true));
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await WriteAsync(conversation, staff, text, recipients.Any(r => r.Minor), ct).ConfigureAwait(false);
            await AuditAsync(Sent, conversation, [conversation.Id], ct, org).ConfigureAwait(false);
            return Result<BroadcastResult>.Success(new BroadcastResult(await ViewAsync(conversation, staff, ct).ConfigureAwait(false), recipients.Count, skipped));
        }, cancellationToken);

    /// <summary>The person's conversations, newest activity first, with how many messages they haven't read.</summary>
    public Task<IReadOnlyList<ConversationSummary>> ListAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ConversationSummary>>(async ct =>
        {
            var mine = await (from p in db.Participants.AsNoTracking()
                              join c in db.Conversations.AsNoTracking() on p.ConversationId equals c.Id
                              where p.UserId == user.Value && p.RemovedAt == null
                              select new { c, p.LastReadAt }).ToListAsync(ct).ConfigureAwait(false);
            List<ConversationSummary> list = [];
            foreach (var x in mine)
            {
                Guid id = x.c.Id;
                DateTimeOffset? last = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id && m.State == MessageStates.Sent)
                    .MaxAsync(m => (DateTimeOffset?)m.SentAt, ct).ConfigureAwait(false);
                DateTimeOffset since = x.LastReadAt ?? DateTimeOffset.MinValue;
                int unread = await db.Messages.AsNoTracking().CountAsync(m => m.ConversationId == id && m.State == MessageStates.Sent
                    && m.SenderUserId != user.Value && m.SentAt > since, ct).ConfigureAwait(false);
                list.Add(new ConversationSummary(id, x.c.OrgId, x.c.ConversationKind, x.c.Subject, x.c.IsOrgVisible, x.c.IncludesMinor, last, unread, x.c.ClosedAt));
            }

            await AuditAsync(Read, null, [.. list.Select(c => c.Id)], ct).ConfigureAwait(false);
            return [.. list.OrderByDescending(c => c.LastMessageAt)];
        }, cancellationToken);

    /// <summary>A conversation's messages, for one of its participants; reading it marks it read.</summary>
    public Task<Result<ConversationView>> GetAsync(UserId user, Guid conversationId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await MembershipAsync(user, conversationId, ct).ConfigureAwait(false) is not { } me)
            {
                return Result<ConversationView>.Failure(NotFound());
            }

            ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == conversationId, ct).ConfigureAwait(false);
            me.LastReadAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Read, conversation, [conversation.Id], ct).ConfigureAwait(false);
            return Result<ConversationView>.Success(await ViewAsync(conversation, user, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>A participant writes into a conversation. An adult's message to a minor is screened first.</summary>
    public Task<Result<MessageView>> SendAsync(UserId user, Guid conversationId, string body, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await MembershipAsync(user, conversationId, ct).ConfigureAwait(false) is not { } me)
            {
                return Result<MessageView>.Failure(NotFound());
            }

            if (Body(body) is not { } text)
            {
                return Result<MessageView>.Failure(BadBody());
            }

            ConversationRecord conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == conversationId, ct).ConfigureAwait(false);
            if (conversation.ClosedAt is not null)
            {
                return Result<MessageView>.Failure(Error.Conflict("messaging.closed", "This conversation has been closed."));
            }

            if (conversation.ConversationKind == ConversationKinds.OrgBroadcast && me.ParticipantRole is ParticipantRoles.Guardian or ParticipantRoles.Youth)
            {
                return Result<MessageView>.Failure(Error.Forbidden("messaging.broadcast", "This is an announcement; it takes no replies."));
            }

            bool screen = conversation.IncludesMinor && !me.IsMinor;
            MessageRecord message = await WriteAsync(conversation, user, text, screen, ct).ConfigureAwait(false);
            me.LastReadAt = message.SentAt;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Sent, conversation, [conversation.Id], ct).ConfigureAwait(false);
            return Result<MessageView>.Success(new MessageView(message.Id, true, me.ParticipantRole, message.Body, message.State, message.SentAt));
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Writes a message (held when screening flags it) and tells the other participants of a sent one.</summary>
    internal async Task<MessageRecord> WriteAsync(ConversationRecord conversation, UserId sender, string text, bool screen, CancellationToken ct)
    {
        IReadOnlyList<string> reasons = screen ? ContactExchangeScreen.Reasons(text) : [];
        MessageRecord message = new()
        {
            Id = UuidV7.NewId(),
            TenantId = conversation.TenantId,
            ConversationId = conversation.Id,
            SenderUserId = sender.Value,
            Body = text,
            State = reasons.Count > 0 ? MessageStates.Held : MessageStates.Sent,
            ScreenResult = screen ? JsonSerializer.Serialize(new { reasons }) : null,
            SentAt = clock.StoredNow(),
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (message.State == MessageStates.Sent)
        {
            string org = await directory.GetOrganisationNameAsync(new OrganisationId(conversation.OrgId), ct).ConfigureAwait(false) ?? "";
            List<Guid> others = await db.Participants.AsNoTracking()
                .Where(p => p.ConversationId == conversation.Id && p.UserId != sender.Value && p.RemovedAt == null && !p.IsImplicitReader)
                .Select(p => p.UserId).ToListAsync(ct).ConfigureAwait(false);
            foreach (Guid other in others)
            {
                await db.PublishAsync(tenant.TenantId, new NotificationRequested(
                    other,
                    NewMessageKind,
                    NewMessageTitle,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["org"] = org },
                    "/messages/" + conversation.Id.ToString("D"),
                    "message:" + message.Id.ToString("N") + ":" + other.ToString("N")), ct).ConfigureAwait(false);
            }
        }

        return message;
    }

    /// <summary>
    /// An adult of the organisation to observe a conversation with a minor: an owner or admin first,
    /// then staff; never one of <paramref name="already"/>, a minor, or someone kept away from any of the young people.
    /// </summary>
    internal async Task<Guid?> ObserverAsync(OrganisationId org, IReadOnlyCollection<Guid> already, IReadOnlyCollection<UserId> youths, CancellationToken ct)
    {
        foreach (OrganisationMembership member in await directory.GetOrganisationMembersAsync(org, ct).ConfigureAwait(false))
        {
            if (already.Contains(member.UserId.Value) || await identity.IsMinorAsync(member.UserId, ct).ConfigureAwait(false))
            {
                continue;
            }

            bool kept = false;
            foreach (UserId youth in youths)
            {
                kept |= await identity.IsContactRestrictedAsync(member.UserId, youth, ct).ConfigureAwait(false);
            }

            if (!kept)
            {
                return member.UserId.Value;
            }
        }

        return null;
    }

    internal static ConversationParticipantRecord Participant(Guid conversation, Guid user, string role, bool isMinor, DateTimeOffset now, bool implicitReader = false) => new()
    {
        ConversationId = conversation,
        UserId = user,
        ParticipantRole = role,
        IsMinor = isMinor,
        IsImplicitReader = implicitReader,
        AddedAt = now,
    };

    private Task<ConversationParticipantRecord?> MembershipAsync(UserId user, Guid conversationId, CancellationToken ct) =>
        db.Participants.FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == user.Value && p.RemovedAt == null, ct);

    private async Task<ConversationView> ViewAsync(ConversationRecord conversation, UserId viewer, CancellationToken ct)
    {
        List<ConversationParticipantRecord> people = await db.Participants.AsNoTracking()
            .Where(p => p.ConversationId == conversation.Id && p.RemovedAt == null).ToListAsync(ct).ConfigureAwait(false);
        Dictionary<Guid, string> roles = people.ToDictionary(p => p.UserId, p => p.ParticipantRole);
        List<MessageRecord> messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && (m.State == MessageStates.Sent || (m.SenderUserId == viewer.Value && m.State == MessageStates.Held)))
            .OrderBy(m => m.SentAt).Take(500).ToListAsync(ct).ConfigureAwait(false);
        // The database marks a conversation with a minor as visible to the organisation at commit;
        // a conversation just made may not show it yet, so read it from the people in it.
        bool readByOrganisation = conversation.IsOrgVisible && (conversation.IncludesMinor || people.Any(p => p.IsMinor) || people.Any(p => p.IsImplicitReader));
        return new ConversationView(conversation.Id, conversation.OrgId, conversation.ConversationKind, conversation.Subject, readByOrganisation,
            [.. people.Select(p => p.ParticipantRole).Distinct().Order(StringComparer.Ordinal)],
            [.. messages.Select(m => new MessageView(m.Id, m.SenderUserId == viewer.Value, roles.GetValueOrDefault(m.SenderUserId, ""), m.Body, m.State, m.SentAt))]);
    }

    private Task AuditAsync(string action, ConversationRecord? conversation, IReadOnlyCollection<Guid> conversations, CancellationToken ct, OrganisationId? org = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "conversation",
            SubjectIds = conversations,
            ActorOrgId = org ?? (conversation is null ? null : new OrganisationId(conversation.OrgId)),
        }, ct);

    private static string? Body(string? body) => body?.Trim() is { Length: > 0 and <= MaxBody } text ? text : null;

    private static Error BadBody() => Error.Validation("messaging.body", $"A message of 1 to {MaxBody} characters.");

    private static Error NotFound() => Error.NotFound("messaging.not_found", "You aren't in a conversation with that id.");
}

/// <summary>Registration of the Community module.</summary>
public static class CommunityModule
{
    /// <summary>The request-side services. The host registers <see cref="CommunityDbContext"/>.</summary>
    public static IServiceCollection AddCommunity(this IServiceCollection services)
    {
        services.AddScoped<Conversations>();
        return services;
    }
}
