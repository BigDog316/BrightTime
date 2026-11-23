using System.Text.Json;
using BrightTime.Abstractions.Messaging;
using BrightTime.Hosting.Localisation;
using BrightTime.Hosting.Mail;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The email an organiser's decision on a listing books (D102), as the worker sends it: what was
/// decided, the organiser's note while the decision stands (encoded, its line breaks kept), and a
/// link to the listing in the console. A note that no longer applies is left out.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ListingDecisionMailTests(DatabaseFixture database)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task Each_decision_is_emailed_with_the_note_that_still_stands()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        Guid org = UuidV7.NewId();
        Guid owner = UuidV7.NewId();
        Guid sentBack = UuidV7.NewId();
        Guid edited = UuidV7.NewId();
        string ownerEmail = $"owner-{owner:N}@example.org";
        string queue = "decisions-" + tenant.ToString("N")[..8];

        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "decisions-" + tenant.ToString("N"));
            await Sql.ExecAsync(connection, null,
                """
                insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed, security_stamp, concurrency_stamp, account_kind, status)
                values (@o, @t, @oe, upper(@oe), @oe, upper(@oe), true, 's', 'c', 'adult', 'active');
                insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind, status, approved_at)
                values (@org, @t, 'Riverbend Swim Inc.', 'Riverbend Swim', @slug, 'nonprofit', 'approved', now());
                insert into opportunity.listing (id, tenant_id, org_id, kind, slug, title, summary, description, status, reviewed_at, review_note, registration_mode, external_url)
                values (@l1, @t, @org, 'program', @s1, 'Lap swim <teens>', 'Swim laps.', 'Swim laps after school.', 'changes_requested', now(),
                        E'Say which pool & when.\nAnd the age range.', 'external', 'https://example.org/register');
                insert into opportunity.listing (id, tenant_id, org_id, kind, slug, title, summary, description, status, published_at, registration_mode, external_url)
                values (@l2, @t, @org, 'program', @s2, 'Diving club', 'Learn to dive.', 'Learn to dive safely.', 'published', now(), 'external', 'https://example.org/register');
                insert into opportunity.listing_pending_edit (listing_id, tenant_id, snapshot, state, saved_by, review_note, reviewed_at, reviewed_by)
                values (@l2, @t, '{}'::jsonb, 'returned', @o, 'Keep the safety line.', now(), @o);
                """,
                [("t", tenant), ("o", owner), ("oe", ownerEmail), ("org", org), ("slug", "riverbend-" + org.ToString("N")[..8]),
                 ("l1", sentBack), ("s1", "lap-" + sentBack.ToString("N")[..8]), ("l2", edited), ("s2", "dive-" + edited.ToString("N")[..8])]);

            // Each as Opportunities books it; the last no longer stands, since the listing is published.
            foreach ((Guid listing, string decision) in new[]
                     {
                         (sentBack, ListingDecisionNotice.ChangesRequested),
                         (edited, ListingDecisionNotice.EditReturned),
                         (edited, ListingDecisionNotice.Unpublished),
                     })
            {
                ListingDecisionNotice notice = new(owner, org, listing, decision, DateTimeOffset.UtcNow);
                await Sql.ExecAsync(connection, null,
                    "insert into platform.job (id, tenant_id, queue, job_type, payload, unique_key) values (@id, @t, @q, @type, @p::jsonb, @k)",
                    [("id", UuidV7.NewId()), ("t", tenant), ("q", queue), ("type", ListingDecisionNotice.JobType),
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
            services.AddScoped<IListingDecisions, ListingDecisions>();
            services.AddJobHandler<ListingDecisionMailHandler>();
        });
        await worker.GetRequiredService<JobRunner>().DrainAsync(Ct);

        mail.Sent.Count.ShouldBe(3);
        mail.Sent.ShouldAllBe(m => m.To == ownerEmail && !m.TextBody.Contains("email.listing_decision", StringComparison.Ordinal));
        mail.Sent.ShouldAllBe(m => m.TextBody.Contains("Riverbend Swim", StringComparison.Ordinal));

        EmailMessage changes = mail.Sent.Single(m => m.Subject.Contains("needs changes", StringComparison.Ordinal));
        changes.Subject.ShouldContain("Lap swim <teens>");
        changes.TextBody.ShouldContain("The organiser's note:\nSay which pool & when.\nAnd the age range.");
        changes.TextBody.ShouldContain($"https://kettle.test/provider/orgs/{org:D}/listings/{sentBack:D}");
        changes.HtmlBody.ShouldContain("Lap swim &lt;teens&gt;");
        changes.HtmlBody.ShouldContain("<blockquote dir=\"auto\">Say which pool &amp; when.<br>And the age range.</blockquote>");

        EmailMessage returned = mail.Sent.Single(m => m.Subject.Contains("were sent back", StringComparison.Ordinal));
        returned.TextBody.ShouldContain("Keep the safety line.");
        returned.TextBody.ShouldContain($"https://kettle.test/provider/orgs/{org:D}/listings/{edited:D}");

        EmailMessage stale = mail.Sent.Single(m => m.Subject.Contains("taken off", StringComparison.Ordinal));
        stale.TextBody.ShouldNotContain("The organiser's note");
        stale.HtmlBody.ShouldNotContain("blockquote");
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
