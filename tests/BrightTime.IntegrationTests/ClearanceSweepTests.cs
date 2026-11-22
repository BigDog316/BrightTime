using BrightTime.Abstractions.Messaging;
using BrightTime.Hosting.Localisation;
using BrightTime.Hosting.Mail;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Clearances;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The nightly clearance sweep (D98): a passed clearance whose expiry has gone is marked expired,
/// one expiring within 45 days is noticed once for that expiry, and later or already-noticed ones
/// are left alone. The organisation's owner gets an inbox notice and an email for each, a
/// county-wide clearance counting at the organisation too; a second sweep finds nothing, and the
/// next night's is booked once.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ClearanceSweepTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task Lapses_are_marked_expiries_noticed_once_and_the_owner_told_by_email()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        Guid org = UuidV7.NewId();
        Guid owner = UuidV7.NewId();
        Guid staff = UuidV7.NewId();
        Guid membership = UuidV7.NewId();
        string ownerEmail = $"owner-{owner:N}@example.org";
        string staffEmail = $"coach-{staff:N}@example.org";
        DateOnly today = SystemClock.Instance.TodayIn(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        DateOnly soon = today.AddDays(10);
        Guid expiring = UuidV7.NewId();
        Guid lapsed = UuidV7.NewId();
        Guid later = UuidV7.NewId();
        Guid noticed = UuidV7.NewId();

        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "clearances-" + tenant.ToString("N"));
            await Sql.ExecAsync(connection, null,
                """
                insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed, security_stamp, concurrency_stamp, account_kind, status)
                values (@o, @t, @oe, upper(@oe), @oe, upper(@oe), true, 's', 'c', 'adult', 'active'),
                       (@s, @t, @se, upper(@se), @se, upper(@se), true, 's', 'c', 'adult', 'active');
                insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind)
                values (@org, @t, 'Riverbend Swim Inc.', 'Riverbend Swim', @slug, 'nonprofit');
                insert into directory.org_membership (id, tenant_id, org_id, user_id, org_role, status, joined_at)
                values (gen_random_uuid(), @t, @org, @o, 'owner', 'active', now()), (@m, @t, @org, @s, 'staff', 'active', now());
                insert into directory.staff_clearance (id, tenant_id, user_id, org_id, clearance_kind, status, completed_on, expires_on, expiry_notice_for)
                values (@c1, @t, @s, @org, 'background_check', 'cleared', @done, @soon, null),
                       (@c2, @t, @s, null, 'mandated_reporter_training', 'cleared', @done, @yesterday, null),
                       (@c3, @t, @s, @org, 'cpr_first_aid', 'conditional', @done, @far, null),
                       (@c4, @t, @s, @org, 'fingerprint', 'cleared', @done, @soon2, @soon2);
                """,
                [("t", tenant), ("o", owner), ("oe", ownerEmail), ("s", staff), ("se", staffEmail), ("org", org), ("slug", "riverbend-" + org.ToString("N")[..8]),
                 ("m", membership), ("c1", expiring), ("c2", lapsed), ("c3", later), ("c4", noticed), ("done", today.AddYears(-2)),
                 ("soon", soon), ("yesterday", today.AddDays(-1)), ("far", today.AddDays(200)), ("soon2", today.AddDays(30))]);
        }

        RecordingMail mail = new();
        string queue = "clearances-" + tenant.ToString("N")[..8];
        await using ServiceProvider worker = TestHost.Worker(Cs, queue, services =>
        {
            string jobs = TestHost.As(Cs, TestHost.JobsLogin);
            services.AddScoped<IAuditWriter>(sp => new AuditTrail(
                sp.GetRequiredService<ITenantContext>(), NoAuditRequestInfo.Instance, sp.GetRequiredService<IClock>(), sp.GetRequiredService<NpgsqlDataSource>()));
            services.AddBrightTimeDbContext<IdentityDbContext>(jobs, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
            services.AddScoped<IIdentityQueries, IdentityQueries>();
            services.AddBrightTimeText(hasDatabase: true);
            services.AddSingleton<ISiteAddress>(new FixedSite());
            services.AddSingleton<IEmailSender>(mail);
            services.AddJobHandler<ClearanceSweepHandler>();
            services.AddJobHandler<ClearanceMailHandler>();
        });

        (await SweepAsync(worker, tenant)).ShouldBe((1, 1));
        await using NpgsqlConnection check = await Sql.OpenAsync(Cs, Ct);
        async Task<string> RowAsync(Guid id) => await Sql.ScalarAsync<string>(check, null,
            "select status || ':' || coalesce(expiry_notice_for::text, '-') from directory.staff_clearance where id = @id", [("id", id)]);
        (await RowAsync(expiring)).ShouldBe($"cleared:{soon:yyyy-MM-dd}");
        (await RowAsync(lapsed)).ShouldBe($"expired:{today.AddDays(-1):yyyy-MM-dd}");
        (await RowAsync(later)).ShouldBe("conditional:-", "two hundred days off is outside the window");
        (await RowAsync(noticed)).ShouldBe($"cleared:{today.AddDays(30):yyyy-MM-dd}", "already noticed for this expiry");
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.audit_event where tenant_id = @t and action = 'directory.clearance.lapsed' and @s = any(subject_ids)",
            [("t", tenant), ("s", staff)])).ShouldBe(1);
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.outbox_message where tenant_id = @t and message_type = 'platform.notification.requested'",
            [("t", tenant)])).ShouldBe(2, "an inbox notice to the owner for each");
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.job where tenant_id = @t and job_type = 'directory.clearance_email'", [("t", tenant)])).ShouldBe(2);
        (await Sql.ScalarAsync<bool>(check, null,
            "select run_after > now() from platform.job where tenant_id = @t and job_type = 'directory.clearance_sweep' and state = 'ready'", [("t", tenant)])).ShouldBeTrue("tomorrow's is booked");

        // Swept again: nothing new, and still one sweep waiting.
        (await SweepAsync(worker, tenant)).ShouldBe((0, 0));
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.job where tenant_id = @t and job_type = 'directory.clearance_email'", [("t", tenant)])).ShouldBe(2);
        (await Sql.ScalarAsync<long>(check, null, "select count(*) from platform.job where tenant_id = @t and job_type = 'directory.clearance_sweep'", [("t", tenant)])).ShouldBe(1);

        // The emails: to the owner, naming the staff member by address, linking to their clearances.
        await Sql.ExecAsync(check, null, "update platform.job set queue = @q where tenant_id = @t and job_type = 'directory.clearance_email'", [("q", queue), ("t", tenant)]);
        await worker.GetRequiredService<JobRunner>().DrainAsync(Ct);
        mail.Sent.Count.ShouldBe(2);
        mail.Sent.ShouldAllBe(m => m.To == ownerEmail);
        mail.Sent.ShouldAllBe(m => m.TextBody.Contains(staffEmail, StringComparison.Ordinal) && m.TextBody.Contains("Riverbend Swim", StringComparison.Ordinal));
        mail.Sent.ShouldAllBe(m => m.TextBody.Contains($"https://kettle.test/provider/orgs/{org:D}/staff/{membership:D}/clearances", StringComparison.Ordinal));
        mail.Sent.ShouldContain(m => m.Subject.StartsWith("Background check for", StringComparison.Ordinal) && m.Subject.Contains("expires on", StringComparison.Ordinal));
        mail.Sent.ShouldContain(m => m.Subject.StartsWith("Mandated reporter training for", StringComparison.Ordinal) && m.Subject.EndsWith("has expired", StringComparison.Ordinal));
        mail.Sent.ShouldAllBe(m => !m.TextBody.Contains("email.clearance", StringComparison.Ordinal));

        await using (AsyncServiceScope scope = worker.CreateAsyncScope())
        {
            await Sql.ExecAsync(check, null, "delete from platform.job where tenant_id = @t", [("t", tenant)]);
            (await ClearanceSweepScheduler.BookAsync(scope.ServiceProvider, new TenantId(tenant), Ct)).ShouldBeTrue();
            (await ClearanceSweepScheduler.BookAsync(scope.ServiceProvider, new TenantId(tenant), Ct)).ShouldBeFalse("a sweep already waiting absorbs the booking");
        }
    }

    private static async Task<(int Warned, int Lapsed)> SweepAsync(ServiceProvider worker, Guid tenant)
    {
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(tenant), TimeZoneInfo.Utc, Locale.English);
        return await scope.ServiceProvider.GetRequiredService<ClearanceSweepHandler>().SweepAsync(bookNext: true, Ct);
    }

    private sealed class FixedSite : ISiteAddress
    {
        public Task<Uri> BaseUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Uri("https://kettle.test/"));
    }

    private sealed class RecordingMail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<DeliveryReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add(message);
            }

            return Task.FromResult(new DeliveryReceipt(true, "test"));
        }
    }
}
