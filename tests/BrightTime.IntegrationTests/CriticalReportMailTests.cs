using System.Text.Json;
using BrightTime.Abstractions.Messaging;
using BrightTime.Hosting.Localisation;
using BrightTime.Hosting.Mail;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The email a critical report books for a moderator (D115), as the worker sends it: the reason in
/// words, to be taken on within the hour, and a link to the moderation queue; nothing of the
/// message. A moderator with no adult address gets none.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CriticalReportMailTests(DatabaseFixture database)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task A_moderator_is_emailed_the_reason_and_the_queue_and_nothing_of_the_message()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        Guid moderator = UuidV7.NewId();
        Guid youth = UuidV7.NewId();
        string email = $"moderator-{moderator:N}@example.org";
        string queue = "reports-" + tenant.ToString("N")[..8];
        Guid report = UuidV7.NewId();

        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "reports-" + tenant.ToString("N"));
            await Sql.ExecAsync(connection, null,
                """
                insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed, security_stamp, concurrency_stamp, account_kind, status)
                values (@m, @t, @me, upper(@me), @me, upper(@me), true, 's', 'c', 'adult', 'active'),
                       (@y, @t, @ye, upper(@ye), @ye, upper(@ye), true, 's', 'c', 'youth', 'active');
                """,
                [("t", tenant), ("m", moderator), ("me", email), ("y", youth), ("ye", $"youth-{youth:N}@example.org")]);

            // One for the moderator, and one for an account with no adult address, which gets none.
            foreach (Guid recipient in new[] { moderator, youth })
            {
                CriticalReportNotice notice = new(recipient, report, ReportReasons.GroomingConcern);
                await Sql.ExecAsync(connection, null,
                    "insert into platform.job (id, tenant_id, queue, job_type, payload, unique_key) values (@id, @t, @q, @type, @p::jsonb, @k)",
                    [("id", UuidV7.NewId()), ("t", tenant), ("q", queue), ("type", CriticalReportNotice.JobType),
                     ("p", JsonSerializer.Serialize(notice, Web)), ("k", notice.UniqueKey)]);
            }
        }

        RecordingMail mail = new();
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
            services.AddJobHandler<CriticalReportMailHandler>();
        });
        await worker.GetRequiredService<JobRunner>().DrainAsync(Ct);

        EmailMessage sent = mail.Sent.ShouldHaveSingleItem();
        sent.To.ShouldBe(email);
        sent.Subject.ShouldBe("A report needs a moderator within the hour");
        sent.TextBody.ShouldContain("made a report: Grooming concern.");
        sent.TextBody.ShouldContain("https://kettle.test/organiser/moderation");
        sent.TextBody.ShouldNotContain("email.critical_report");
        sent.HtmlBody.ShouldContain("<a href=\"https://kettle.test/organiser/moderation\">Open the moderation queue</a>");
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
