using System.Collections.Concurrent;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.Modules.Notifications;
using BrightTime.Modules.Notifications.Persistence;
using BrightTime.Modules.Opportunities.Personal;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// A reminder, end to end on the worker: the reminder's job asks to notify, the outbox delivers it,
/// and Notifications writes the inbox row and pushes to the person's phone, unless they asked for
/// quiet. On the seeded Kettle tenant, in a database of its own.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class NotificationDeliveryTests(DatabaseFixture database)
{
    private const string DatabaseName = "brighttime_notifytest";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records pushes instead of sending them.</summary>
    private sealed class RecordingPushSender : IPushSender
    {
        public ConcurrentQueue<(string Platform, string Token, string TitleKey)> Sent { get; } = new();

        public Task<bool> SendAsync(string platform, string token, string kind, string titleKey, IReadOnlyDictionary<string, string> arguments, string? deepLink, CancellationToken cancellationToken)
        {
            Sent.Enqueue((platform, token, titleKey));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task A_due_reminder_reaches_the_inbox_and_the_phone_once_and_quiet_hours_hold_the_push()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (string cs, SeedData data, _) = await KettleDatabase.CreateAsync(database.ConnectionString, DatabaseName, Ct);
        Guid tenant = data.TenantId;
        Guid user = SeedIds.For("user/admin@" + data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).First().Slug + ".example.org");
        SeedListing listing = data.Listings.First(l => l.Status == "published");

        string queue = "notify-" + Guid.NewGuid().ToString("N")[..8];
        var push = new RecordingPushSender();
        IConfiguration keys = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:DataKeys:0:Version"] = "1",
            ["Security:DataKeys:0:KeyRef"] = "test-master",
            ["Security:Keys:test-master"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        }).Build();

        await using ServiceProvider worker = TestHost.Worker(cs, queue, services =>
        {
            string jobs = TestHost.As(cs, TestHost.JobsLogin);
            services.Configure<OutboxOptions>(o => o.Queue = queue);
            services.AddBrightTimeDbContext<IdentityDbContext>(jobs, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
            services.AddScoped<IIdentityQueries, IdentityQueries>();
            services.AddBrightTimeDbContext<NotificationsDbContext>(jobs, NotificationsDbContext.SchemaName, NotificationsDbContext.MigrationsHistoryTable);
            services.AddFieldEncryption(keys, development: false);
            services.AddReminderJobs();
            services.AddNotificationDelivery();
            services.AddSingleton<IPushSender>(push);
        });

        // The person's phone, sealed as the API seals it.
        const string Token = "fcm-token-For-This-Phone";
        byte[] hash = await worker.GetRequiredService<HmacTokenHasher>().HashAsync(new TenantId(tenant), PushDevices.TokenPurpose, Token, Ct);
        byte[] sealedToken = (await worker.GetRequiredService<IFieldCipher>().EncryptAsync(new TenantId(tenant), Token, Ct))!;
        Guid reminder = UuidV7.NewId();
        await using (NpgsqlConnection connection = await Sql.OpenAsync(cs, Ct))
        {
            await Sql.ExecAsync(connection, null,
                """
                insert into notify.push_device (id, tenant_id, user_id, platform_kind, token_hash, token_enc)
                values (gen_random_uuid(), @t, @u, 'android', @h, @e);
                insert into opportunity.listing_reminder (id, tenant_id, user_id, listing_id, anchor, lead_minutes, remind_at)
                values (@r, @t, @u, @l, 'next_session', 60, now());
                insert into platform.job (id, tenant_id, queue, job_type, payload, run_after)
                values (gen_random_uuid(), @t, @q, @type, jsonb_build_object('reminderId', @r::text), now());
                """,
                [("t", tenant), ("u", user), ("h", hash), ("e", sealedToken), ("r", reminder), ("l", listing.Id),
                 ("q", queue), ("type", ReminderDueHandler.JobType)]);
        }

        await TestHost.DrainAsync(worker, Ct);

        await using NpgsqlConnection check = await Sql.OpenAsync(cs, Ct);
        (await Sql.ScalarAsync<bool>(check, null, "select sent_at is not null from opportunity.listing_reminder where id = @r", [("r", reminder)])).ShouldBeTrue();
        (await Sql.ScalarAsync<string>(check, null, "select title || ' ' || deep_link from notify.notification where user_id = @u", [("u", user)]))
            .ShouldBe($"{ReminderDueHandler.SessionTitle} /listings/{listing.Slug}");
        push.Sent.ShouldHaveSingleItem().ShouldBe(("android", Token, ReminderDueHandler.SessionTitle));

        // Asked again with the same key, as a retried delivery would: nothing new.
        await HandleAsync(worker, tenant, Request(user, "reminder:" + reminder.ToString("N")));
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from notify.notification where user_id = @u", [("u", user)])).ShouldBe(1);
        push.Sent.Count.ShouldBe(1);

        // In quiet hours the inbox still gets it; the phone stays silent.
        await Sql.ExecAsync(check, null,
            """
            insert into notify.preference (user_id, tenant_id, notification_kind, channel, is_enabled, quiet_from_local, quiet_to_local)
            values (@u, @t, 'session_reminder', 'push', true, '00:00', '23:59')
            """,
            [("u", user), ("t", tenant)]);
        await HandleAsync(worker, tenant, Request(user, "quiet-" + Guid.NewGuid().ToString("N")));
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from notify.notification where user_id = @u", [("u", user)])).ShouldBe(2);
        push.Sent.Count.ShouldBe(1);
    }

    private static Modules.Platform.Contracts.Messages.NotificationRequested Request(Guid user, string dedupe) =>
        new(user, "session_reminder", ReminderDueHandler.SessionTitle, new Dictionary<string, string>(), "/listings/x", dedupe);

    private static async Task HandleAsync(ServiceProvider worker, Guid tenant, Modules.Platform.Contracts.Messages.NotificationRequested message)
    {
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(tenant), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), SharedKernel.Primitives.Locale.English);
        await ActivatorUtilities.CreateInstance<NotificationRequestedHandler>(scope.ServiceProvider).HandleAsync(message, Ct);
    }
}
