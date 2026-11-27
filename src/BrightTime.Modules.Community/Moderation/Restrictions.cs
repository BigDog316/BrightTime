using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Moderation;

/// <summary>A measure against a person, as the moderator reads it (D119): never with their name.</summary>
public sealed record RestrictionView(
    Guid Id, string Kind, string Reason, string ImposedBy, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset? LiftedAt, string? LiftedBy, string? LiftReason, bool InForce);

/// <summary>
/// Muting or suspending an adult for a time (D119), from a message of theirs a moderator is
/// looking at. A mute keeps them from writing in any conversation with a young person; a
/// suspension keeps their account from signing in or acting (Identity's
/// <see cref="IAccountSuspensions"/>). Each lasts 24 hours, 7 days or 30 days, unless a moderator
/// lifts it sooner; both need a reason, and each is a <c>person_restriction</c> and a
/// <c>moderation_action</c> (<c>mute</c> or <c>suspend_user</c>, and <c>restore</c> when lifted).
/// </summary>
/// <remarks>
/// Adults only (D119): a young person's account isn't muted or suspended here. Nobody of the
/// conversation's organisation decides, and nobody acts on themselves. A suspension is booked with
/// Identity first and recorded here after; if the record then fails, the suspension is lifted
/// again, so there's never one without its reason.
/// </remarks>
public sealed class Restrictions(
    CommunityDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IAccountSuspensions suspensions)
{
    public const string Mute = "mute";
    public const string Suspend = "suspend";

    public const string Imposed = "moderation.person_restricted";
    public const string Lifted = "moderation.person_restriction_lifted";

    public const int MaxReason = 1000;

    /// <summary>The lengths a moderator chooses from.</summary>
    public static IReadOnlyDictionary<string, TimeSpan> Lengths { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
    };

    /// <summary>Whether a mute keeps <paramref name="user"/> from writing to young people at <paramref name="now"/>.</summary>
    internal static Task<bool> IsMutedAsync(CommunityDbContext db, Guid user, DateTimeOffset now, CancellationToken ct) =>
        db.PersonRestrictions.AsNoTracking()
            .AnyAsync(r => r.UserId == user && r.Kind == Mute && r.LiftedAt == null && r.EndsAt > now, ct);

    /// <summary>Everything done to the sender of <paramref name="messageId"/>, newest first; null when the moderator may not see it.</summary>
    public Task<IReadOnlyList<RestrictionView>?> OfSenderAsync(UserId moderator, Guid messageId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<RestrictionView>?>(async ct =>
        {
            if (await SenderAsync(moderator, messageId, ct).ConfigureAwait(false) is not { Error: null } sender)
            {
                return null;
            }

            List<PersonRestrictionRecord> rows = await db.PersonRestrictions.AsNoTracking()
                .Where(r => r.UserId == sender.User!.Value.Value).OrderByDescending(r => r.StartsAt).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyDictionary<Guid, string> names = await identity.GetAdultEmailsAsync(
                [.. rows.Select(r => r.ImposedBy), .. rows.Where(r => r.LiftedBy is not null).Select(r => r.LiftedBy!.Value)], ct).ConfigureAwait(false);
            DateTimeOffset now = clock.StoredNow();
            return [.. rows.Select(r => new RestrictionView(r.Id, r.Kind, r.Reason, names.GetValueOrDefault(r.ImposedBy, ""), r.StartsAt, r.EndsAt,
                r.LiftedAt, r.LiftedBy is { } l ? names.GetValueOrDefault(l, "") : null, r.LiftReason, r.LiftedAt is null && r.EndsAt > now))];
        }, cancellationToken);

    /// <summary>Mutes or suspends the sender of <paramref name="messageId"/> for one of <see cref="Lengths"/>.</summary>
    public Task<Result> ImposeAsync(UserId moderator, Guid messageId, string? kind, string? length, string? reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (kind is not (Mute or Suspend) || length is null || !Lengths.TryGetValue(length, out TimeSpan span))
            {
                return Result.Failure(Error.Validation("moderation.restriction", "Choose to mute or suspend, and for how long."));
            }

            if (reason?.Trim() is not { Length: > 0 and <= MaxReason } why)
            {
                return Result.Failure(Error.Validation("moderation.reason", $"Give a reason of up to {MaxReason} characters; it's kept in the moderation record."));
            }

            (UserId? who, Error? refused) = await SenderAsync(moderator, messageId, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return Result.Failure(refused);
            }

            UserId sender = who!.Value;
            DateTimeOffset now = clock.StoredNow();
            DateTimeOffset ends = now + span;
            if (kind == Suspend && !await suspensions.SuspendAsync(sender, ends, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("moderation.not_suspendable", "This account can't be suspended: it isn't an active adult's."));
            }

            try
            {
                PersonRestrictionRecord restriction = new()
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    UserId = sender.Value,
                    Kind = kind,
                    Reason = why,
                    ImposedBy = moderator.Value,
                    SourceMessageId = messageId,
                    StartsAt = now,
                    EndsAt = ends,
                };
                db.PersonRestrictions.Add(restriction);
                db.ModerationActions.Add(Action(sender, kind == Mute ? "mute" : "suspend_user", moderator, why, now, (int)span.TotalHours));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await AuditAsync(Imposed, sender, kind, ct).ConfigureAwait(false);
            }
            catch when (kind == Suspend)
            {
                await suspensions.LiftAsync(sender, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            return Result.Success();
        }, cancellationToken);

    /// <summary>Ends a mute or suspension before its time, with a reason.</summary>
    public Task<Result> LiftAsync(UserId moderator, Guid restrictionId, string? reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (reason?.Trim() is not { Length: > 0 and <= MaxReason } why)
            {
                return Result.Failure(Error.Validation("moderation.reason", $"Give a reason of up to {MaxReason} characters; it's kept in the moderation record."));
            }

            DateTimeOffset now = clock.StoredNow();
            PersonRestrictionRecord? restriction = await db.PersonRestrictions
                .FirstOrDefaultAsync(r => r.Id == restrictionId && r.LiftedAt == null && r.EndsAt > now, ct).ConfigureAwait(false);
            if (restriction is null)
            {
                return Result.Failure(Error.NotFound("moderation.not_in_force", "That mute or suspension has already ended."));
            }

            if (restriction.SourceMessageId is { } source && await SenderAsync(moderator, source, ct).ConfigureAwait(false) is { Error: { } refused })
            {
                return Result.Failure(refused);
            }

            restriction.LiftedAt = now;
            restriction.LiftedBy = moderator.Value;
            restriction.LiftReason = why;
            db.ModerationActions.Add(Action(new UserId(restriction.UserId), "restore", moderator, why, now, null));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // The account is active again only if no other suspension of it is still running.
            if (restriction.Kind == Suspend && !await db.PersonRestrictions.AnyAsync(
                    r => r.UserId == restriction.UserId && r.Kind == Suspend && r.LiftedAt == null && r.EndsAt > now, ct).ConfigureAwait(false))
            {
                await suspensions.LiftAsync(new UserId(restriction.UserId), ct).ConfigureAwait(false);
            }

            await AuditAsync(Lifted, new UserId(restriction.UserId), restriction.Kind, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The message's sender, if this moderator may act on them: an adult, not themselves, not of the conversation's organisation.</summary>
    private async Task<(UserId? User, Error? Error)> SenderAsync(UserId moderator, Guid messageId, CancellationToken ct)
    {
        if (await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct).ConfigureAwait(false) is not { } message)
        {
            return (null, Error.NotFound("moderation.no_message", "There's no message like that."));
        }

        Guid org = await db.Conversations.AsNoTracking().Where(c => c.Id == message.ConversationId).Select(c => c.OrgId).FirstAsync(ct).ConfigureAwait(false);
        if (message.SenderUserId == moderator.Value
            || (await directory.GetOrganisationMembersAsync(new OrganisationId(org), ct).ConfigureAwait(false)).Any(m => m.UserId == moderator))
        {
            return (null, Error.Forbidden("moderation.own_organisation", "You're a member of the organisation that wrote this message, so another moderator decides it."));
        }

        UserId sender = new(message.SenderUserId);
        return await identity.IsMinorAsync(sender, ct).ConfigureAwait(false)
            ? (null, Error.Conflict("moderation.adults_only", "Only an adult's account is muted or suspended here."))
            : (sender, null);
    }

    private ModerationActionRecord Action(UserId target, string action, UserId moderator, string reason, DateTimeOffset at, int? hours) => new()
    {
        Id = UuidV7.NewId(),
        TenantId = tenant.TenantId.Value,
        TargetKind = "user",
        TargetId = target.Value,
        Action = action,
        ActorUserId = moderator.Value,
        Reason = reason,
        DurationHours = hours,
        OccurredAt = at,
    };

    private Task AuditAsync(string action, UserId person, string kind, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "user",
            SubjectIds = [person.Value],
            Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = kind },
        }, ct);
}
