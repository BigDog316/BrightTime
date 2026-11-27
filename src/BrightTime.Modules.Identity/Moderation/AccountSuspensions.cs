using System.Security.Cryptography;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Domain;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Identity.Moderation;

/// <summary>
/// Suspends an adult's account for a time (D119). The account's status is <c>suspended</c>,
/// which every way of signing in already refuses; its security stamp changes and its refresh
/// tokens are revoked, so a cookie session or an access token stops at its next check; and a job
/// is booked for <c>suspended_until</c> that makes it active again.
/// </summary>
/// <remarks>
/// A suspension never shortens one already running: the later end stands, and each end's job
/// finds the account still suspended only if its own time has come. Young people's and children's
/// accounts aren't suspended here (D119: adults only).
/// </remarks>
public sealed class AccountSuspensions(IdentityDbContext db, ITenantContext tenant) : IAccountSuspensions
{
    public Task<bool> SuspendAsync(UserId user, DateTimeOffset until, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            int changed = await db.Database.ExecuteSqlAsync(
                $"""
                update identity.app_user
                set status = {UserStatus.Suspended},
                    suspended_until = greatest(coalesce(suspended_until, {until}), {until}),
                    security_stamp = {stamp}, updated_at = now()
                where id = {user.Value} and account_kind = {AccountKind.Adult} and deleted_at is null
                  and (status = {UserStatus.Active} or (status = {UserStatus.Suspended} and suspended_until is not null))
                """, ct).ConfigureAwait(false);
            if (changed == 0)
            {
                return false;
            }

            await db.Database.ExecuteSqlAsync(
                $"update identity.refresh_token set revoked_at = now(), revoked_reason = 'suspended' where user_id = {user.Value} and revoked_at is null",
                ct).ConfigureAwait(false);
            SuspensionEnds ends = new(user.Value, until);
            await db.EnqueueJobAsync(new JobRequest
            {
                JobType = SuspensionEnds.JobType,
                TenantId = tenant.TenantId,
                Payload = ends,
                UniqueKey = ends.UniqueKey,
                RunAfter = until,
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<bool> LiftAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct => await ReactivateAsync(db, user.Value, null, ct).ConfigureAwait(false) > 0, cancellationToken);

    /// <summary>
    /// Makes a moderator-suspended account active again: now, or (<paramref name="dueBy"/>) only
    /// if its suspension has ended by then.
    /// </summary>
    internal static Task<int> ReactivateAsync(IdentityDbContext db, Guid user, DateTimeOffset? dueBy, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
            update identity.app_user
            set status = {UserStatus.Active}, suspended_until = null, updated_at = now()
            where id = {user} and status = {UserStatus.Suspended} and suspended_until is not null
              and ({dueBy}::timestamptz is null or suspended_until <= {dueBy})
            """, ct);
}

/// <summary>A suspension's end (D119): the account is active again, unless a longer one now runs.</summary>
public sealed class SuspensionEndsHandler(IdentityDbContext db, IClock clock) : IJobHandler
{
    public static string JobType => SuspensionEnds.JobType;

    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SuspensionEnds ends = context.ReadPayload<SuspensionEnds>();
        return db.InTransactionAsync(async ct => await AccountSuspensions.ReactivateAsync(db, ends.UserId, clock.UtcNow, ct).ConfigureAwait(false), cancellationToken);
    }
}

public static class SuspensionModule
{
    /// <summary>Suspending accounts, for the request side (the moderation console).</summary>
    public static IServiceCollection AddAccountSuspensions(this IServiceCollection services)
    {
        services.AddScoped<IAccountSuspensions, AccountSuspensions>();
        return services;
    }

    /// <summary>The worker's half: each suspension's end.</summary>
    public static IServiceCollection AddSuspensionJobs(this IServiceCollection services)
    {
        services.AddJobHandler<SuspensionEndsHandler>();
        return services;
    }
}
