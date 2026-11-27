using System.Text.Json;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Moderation;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// A suspension's end (D119), as the worker runs it: an account whose suspension is over is
/// active again; one suspended for longer since stays suspended; one an administrator suspended
/// without an end is left alone.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SuspensionEndsTests(DatabaseFixture database)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task An_ended_suspension_makes_the_account_active_and_a_longer_one_stands()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await TestHost.EnsureLoginsAsync(Cs, Ct);
        Guid tenant = UuidV7.NewId();
        Guid over = UuidV7.NewId();
        Guid longer = UuidV7.NewId();
        Guid indefinite = UuidV7.NewId();
        string queue = "suspensions-" + tenant.ToString("N")[..8];
        DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct))
        {
            await Sql.SeedTenantAsync(connection, tenant, "suspensions-" + tenant.ToString("N"));
            foreach ((Guid id, string until) in new[] { (over, "now() - interval '1 minute'"), (longer, "now() + interval '7 days'"), (indefinite, "null") })
            {
                string email = $"adult-{id:N}@example.org";
                await Sql.ExecAsync(connection, null,
                    $"""
                    insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed, security_stamp, concurrency_stamp, account_kind, status, suspended_until)
                    values (@id, @t, @e, upper(@e), @e, upper(@e), true, 's', 'c', 'adult', 'suspended', {until})
                    """,
                    [("id", id), ("t", tenant), ("e", email)]);

                // Each booked for a time that has come: the first suspension's end.
                SuspensionEnds ends = new(id, past);
                await Sql.ExecAsync(connection, null,
                    "insert into platform.job (id, tenant_id, queue, job_type, payload, unique_key) values (@id, @t, @q, @type, @p::jsonb, @k)",
                    [("id", UuidV7.NewId()), ("t", tenant), ("q", queue), ("type", SuspensionEnds.JobType),
                     ("p", JsonSerializer.Serialize(ends, Web)), ("k", ends.UniqueKey)]);
            }
        }

        await using ServiceProvider worker = TestHost.Worker(Cs, queue, services =>
        {
            services.AddBrightTimeDbContext<IdentityDbContext>(TestHost.As(Cs, TestHost.JobsLogin), IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
            services.AddSuspensionJobs();
        });
        await worker.GetRequiredService<JobRunner>().DrainAsync(Ct);

        await using NpgsqlConnection check = await Sql.OpenAsync(Cs, Ct);
        foreach ((Guid id, string expected) in new[] { (over, "active/true"), (longer, "suspended/false"), (indefinite, "suspended/true") })
        {
            (await Sql.ScalarAsync<string>(check, null, "select status || '/' || (suspended_until is null)::text from identity.app_user where id = @id", [("id", id)]))
                .ShouldBe(expected, id.ToString());
        }
    }
}
