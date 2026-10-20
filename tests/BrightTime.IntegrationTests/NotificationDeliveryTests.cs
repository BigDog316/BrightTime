using System.Collections.Concurrent;
using BrightTime.Abstractions.Security;
using BrightTime.Hosting.Localisation;
using BrightTime.Infrastructure.Auditing;
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
using BrightTime.SharedKernel.Time;
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
        public ConcurrentQueue<PushMessage> Sent { get; } = new();

        public Task<PushOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            return Task.FromResult(Gone.Contains(message.Token) ? PushOutcome.Unregistered : PushOutcome.Sent);
        }

        /// <summary>Tokens the provider says it no longer knows.</summary>
        public HashSet<string> Gone { get; } = new(StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_due_reminder_reaches_the_inbox_and_each_phone_in_its_language_once_and_quiet_hours_hold_the_push()
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
            services.AddScoped<IAuditWriter>(sp => new AuditTrail(
                sp.GetRequiredService<ITenantContext>(), NoAuditRequestInfo.Instance, sp.GetRequiredService<IClock>(), sp.GetRequiredService<NpgsqlDataSource>()));
            services.AddBrightTimeText(hasDatabase: true);
            services.AddReminderJobs();
            services.AddNotificationDelivery();
            services.AddSingleton<IPushSender>(push);
        });

        // The person's phones, sealed as the API seals them: one with no language, one in Spanish.
        const string Token = "fcm-token-For-This-Phone";
        const string SpanishToken = "fcm-token-For-The-Other-Phone";
        HmacTokenHasher hasher = worker.GetRequiredService<HmacTokenHasher>();
        IFieldCipher cipher = worker.GetRequiredService<IFieldCipher>();
        byte[] hash = await hasher.HashAsync(new TenantId(tenant), PushDevices.TokenPurpose, Token, Ct);
        byte[] sealedToken = (await cipher.EncryptAsync(new TenantId(tenant), Token, Ct))!;
        byte[] spanishHash = await hasher.HashAsync(new TenantId(tenant), PushDevices.TokenPurpose, SpanishToken, Ct);
        byte[] spanishSealed = (await cipher.EncryptAsync(new TenantId(tenant), SpanishToken, Ct))!;
        Guid reminder = UuidV7.NewId();
        await using (NpgsqlConnection connection = await Sql.OpenAsync(cs, Ct))
        {
            await Sql.ExecAsync(connection, null,
                """
                insert into notify.push_device (id, tenant_id, user_id, platform_kind, token_hash, token_enc, locale)
                values (gen_random_uuid(), @t, @u, 'android', @h, @e, null),
                       (gen_random_uuid(), @t, @u, 'ios', @sh, @se, 'es');
                insert into opportunity.listing_reminder (id, tenant_id, user_id, listing_id, anchor, lead_minutes, remind_at)
                values (@r, @t, @u, @l, 'next_session', 60, now());
                insert into platform.job (id, tenant_id, queue, job_type, payload, run_after)
                values (gen_random_uuid(), @t, @q, @type, jsonb_build_object('reminderId', @r::text), now());
                """,
                [("t", tenant), ("u", user), ("h", hash), ("e", sealedToken), ("sh", spanishHash), ("se", spanishSealed), ("r", reminder), ("l", listing.Id),
                 ("q", queue), ("type", ReminderDueHandler.JobType)]);
        }

        await TestHost.DrainAsync(worker, Ct);

        await using NpgsqlConnection check = await Sql.OpenAsync(cs, Ct);
        (await Sql.ScalarAsync<bool>(check, null, "select sent_at is not null from opportunity.listing_reminder where id = @r", [("r", reminder)])).ShouldBeTrue();
        (await Sql.ScalarAsync<string>(check, null, "select title || ' ' || deep_link from notify.notification where user_id = @u", [("u", user)]))
            .ShouldBe($"{ReminderDueHandler.SessionTitle} /listings/{listing.Slug}");
        // Worded on the way out: the tenant's language for the phone with none, Spanish for the other.
        push.Sent.Count.ShouldBe(2);
        PushMessage english = push.Sent.Single(m => m.Token == Token);
        english.Platform.ShouldBe("android");
        english.Title.ShouldBe("Starting soon");
        english.Body.ShouldStartWith("⁨" + listing.Title + "⁩ starts on ");
        english.DeepLink.ShouldBe($"/listings/{listing.Slug}");
        PushMessage spanish = push.Sent.Single(m => m.Token == SpanishToken);
        spanish.Title.ShouldBe("Empieza pronto");
        spanish.Body.ShouldNotContain("{");

        // Asked again with the same key, as a retried delivery would: nothing new.
        await HandleAsync(worker, tenant, Request(user, "reminder:" + reminder.ToString("N")));
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from notify.notification where user_id = @u", [("u", user)])).ShouldBe(1);
        push.Sent.Count.ShouldBe(2);

        // In quiet hours the inbox still gets it; the phone stays silent.
        await Sql.ExecAsync(check, null,
            """
            insert into notify.preference (user_id, tenant_id, notification_kind, channel, is_enabled, quiet_from_local, quiet_to_local)
            values (@u, @t, 'session_reminder', 'push', true, '00:00', '23:59')
            """,
            [("u", user), ("t", tenant)]);
        await HandleAsync(worker, tenant, Request(user, "quiet-" + Guid.NewGuid().ToString("N")));
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from notify.notification where user_id = @u", [("u", user)])).ShouldBe(2);
        push.Sent.Count.ShouldBe(2);

        // Out of quiet hours again, and the provider has forgotten the Spanish phone's token: that
        // device is revoked, and the next notification goes to the other phone alone.
        await Sql.ExecAsync(check, null, "delete from notify.preference where user_id = @u", [("u", user)]);
        push.Gone.Add(SpanishToken);
        await HandleAsync(worker, tenant, Request(user, "gone-" + Guid.NewGuid().ToString("N")));
        (await Sql.ScalarAsync<bool>(check, null, "select revoked_at is not null from notify.push_device where token_hash = @h", [("h", spanishHash)])).ShouldBeTrue();
        int before = push.Sent.Count;
        await HandleAsync(worker, tenant, Request(user, "after-" + Guid.NewGuid().ToString("N")));
        push.Sent.Skip(before).ShouldHaveSingleItem().Token.ShouldBe(Token);
    }

    private static Modules.Platform.Contracts.Messages.NotificationRequested Request(Guid user, string dedupe) =>
        new(user, "session_reminder", ReminderDueHandler.SessionTitle, new Dictionary<string, string> { ["listing"] = "x", ["at"] = "2026-10-21T20:00:00Z" }, "/listings/x", dedupe);

    private static async Task HandleAsync(ServiceProvider worker, Guid tenant, Modules.Platform.Contracts.Messages.NotificationRequested message)
    {
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(tenant), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), SharedKernel.Primitives.Locale.English);
        await ActivatorUtilities.CreateInstance<NotificationRequestedHandler>(scope.ServiceProvider).HandleAsync(message, Ct);
    }
}
