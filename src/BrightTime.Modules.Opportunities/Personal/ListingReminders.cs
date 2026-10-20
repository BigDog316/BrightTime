using System.Globalization;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Opportunities.Personal;

public sealed record ReminderView(Guid Id, Guid ListingId, string Anchor, int LeadMinutes, DateTimeOffset RemindAt, bool Sent);

/// <summary>
/// Reminders before a listing's next deadline or next session (docs/plan/05, <c>/me/reminders</c>).
/// Each books a job for the moment it is due, in the same transaction; the job asks the
/// Notifications module to tell the person (<see cref="NotificationRequested"/>), whose
/// preferences and quiet hours decide how.
/// </summary>
/// <remarks>
/// The web keeps reminders as calendar alarms (D22), which need no account. These are for the
/// app, where a person is signed in and may be pushed.
/// </remarks>
public sealed class ListingReminders(OpportunitiesDbContext db, ITenantContext tenant, IClock clock)
{
    public const string Deadline = "deadline";
    public const string NextSession = "next_session";
    public const int MaxReminders = 50;
    public const int MaxLeadMinutes = 43_200;

    public Task<IReadOnlyList<ReminderView>> ListAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ReminderView>>(async ct => await db.ListingReminders.AsNoTracking()
            .Where(r => r.UserId == user.Value)
            .OrderBy(r => r.RemindAt)
            .Select(r => new ReminderView(r.Id, r.ListingId, r.Anchor, r.LeadMinutes, r.RemindAt, r.SentAt != null))
            .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    /// <summary>Books a reminder <paramref name="leadMinutes"/> before the listing's next deadline or session.</summary>
    public Task<Result<ReminderView>> CreateAsync(UserId user, Guid listingId, string anchor, int leadMinutes, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (anchor is not (Deadline or NextSession))
            {
                return Result<ReminderView>.Failure(Error.Validation("reminders.anchor", "Remind before the deadline or the next session."));
            }

            if (leadMinutes is < 0 or > MaxLeadMinutes)
            {
                return Result<ReminderView>.Failure(Error.Validation("reminders.lead", "From 0 minutes to 30 days before."));
            }

            DateTimeOffset now = clock.UtcNow;
            string published = ListingStatus.Published.ToDbValue();
            bool live = await db.Listings.AsNoTracking().AnyAsync(
                l => l.Id == listingId && l.Status == published
                     && (l.PublishAt == null || l.PublishAt <= now) && (l.ExpiresAt == null || l.ExpiresAt > now), ct).ConfigureAwait(false);
            if (!live)
            {
                return Result<ReminderView>.Failure(Error.NotFound("reminders.listing_not_found", "No published listing has that id."));
            }

            DateTimeOffset? target = anchor == Deadline
                ? await db.ListingDeadlines.AsNoTracking().Where(d => d.ListingId == listingId && d.DueAt > now)
                    .OrderBy(d => d.DueAt).Select(d => (DateTimeOffset?)d.DueAt).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                : await db.Occurrences.AsNoTracking().Where(o => o.ListingId == listingId && o.StartsAt > now && !o.IsCancelled)
                    .OrderBy(o => o.StartsAt).Select(o => (DateTimeOffset?)o.StartsAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (target is null)
            {
                return Result<ReminderView>.Failure(Error.Conflict("reminders.nothing_ahead", "There is no upcoming one to be reminded of."));
            }

            DateTimeOffset remindAt = target.Value.AddMinutes(-leadMinutes);
            if (remindAt <= now)
            {
                return Result<ReminderView>.Failure(Error.Conflict("reminders.too_late", "That moment has already passed. Choose a shorter time before."));
            }

            if (await db.ListingReminders.CountAsync(r => r.UserId == user.Value && r.SentAt == null, ct).ConfigureAwait(false) >= MaxReminders)
            {
                return Result<ReminderView>.Failure(Error.Conflict("reminders.too_many", $"At most {MaxReminders} reminders waiting."));
            }

            ListingReminderRecord reminder = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                UserId = user.Value,
                ListingId = listingId,
                Anchor = anchor,
                LeadMinutes = leadMinutes,
                RemindAt = remindAt,
            };
            db.ListingReminders.Add(reminder);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.EnqueueJobAsync(new JobRequest
            {
                JobType = ReminderDueHandler.JobType,
                TenantId = tenant.TenantId,
                Payload = new ReminderDueHandler.Payload(reminder.Id),
                UniqueKey = $"{ReminderDueHandler.JobType}:{reminder.Id:N}",
                RunAfter = remindAt,
            }, ct).ConfigureAwait(false);

            return Result<ReminderView>.Success(new ReminderView(reminder.Id, listingId, anchor, leadMinutes, remindAt, false));
        }, cancellationToken);

    /// <summary>Cancels one of the person's reminders. Its job finds nothing when it runs.</summary>
    public Task<bool> DeleteAsync(UserId user, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await db.ListingReminders.Where(r => r.Id == id && r.UserId == user.Value)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0, cancellationToken);
}

/// <summary>
/// A reminder is due: ask Notifications to tell the person, and mark it sent, in one transaction.
/// A reminder deleted meanwhile, or already sent, is skipped; the notification's dedupe key makes
/// a retry harmless.
/// </summary>
public sealed class ReminderDueHandler(OpportunitiesDbContext db, IClock clock) : IJobHandler
{
    public static string JobType => "opportunities.reminder_due";

    /// <summary>The titles the app renders. They join the shared catalogues with the app's strings.</summary>
    public const string DeadlineTitle = "notify.reminder.deadline";
    public const string SessionTitle = "notify.reminder.session";

    public sealed record Payload(Guid ReminderId);

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TenantId tenant = context.TenantId ?? throw new InvalidOperationException("A reminder must belong to a tenant.");
        Payload payload = context.ReadPayload<Payload>();

        await db.InTransactionAsync(async ct =>
        {
            ListingReminderRecord? reminder = await db.ListingReminders.FirstOrDefaultAsync(r => r.Id == payload.ReminderId, ct).ConfigureAwait(false);
            if (reminder is null || reminder.SentAt is not null)
            {
                return 0;
            }

            var listing = await db.Listings.AsNoTracking()
                .Where(l => l.Id == reminder.ListingId)
                .Select(l => new { l.Slug, l.Title })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (listing is null)
            {
                return 0;
            }

            DateTimeOffset target = reminder.RemindAt.AddMinutes(reminder.LeadMinutes);
            bool deadline = reminder.Anchor == ListingReminders.Deadline;
            await db.PublishAsync(tenant, new NotificationRequested(
                reminder.UserId,
                deadline ? "deadline_reminder" : "session_reminder",
                deadline ? DeadlineTitle : SessionTitle,
                new Dictionary<string, string>
                {
                    ["listing"] = listing.Title,
                    ["at"] = target.ToString("O", CultureInfo.InvariantCulture),
                },
                "/listings/" + listing.Slug,
                "reminder:" + reminder.Id.ToString("N"),
                ExpiresAt: target), ct).ConfigureAwait(false);

            reminder.SentAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return 1;
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Registration for the worker host.</summary>
public static class ReminderJobs
{
    public static IServiceCollection AddReminderJobs(this IServiceCollection services)
    {
        services.AddJobHandler<ReminderDueHandler>();
        return services;
    }
}
