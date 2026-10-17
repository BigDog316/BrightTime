using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Auditing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BrightTime.Web.Tests;

/// <summary>
/// The audit trail as the web host writes it: authentication events, what each row holds about
/// the client, the organiser's explorer, and the [YouthRecord] safety net.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed partial class AuditTests(ConsoleFixture db) : IAsyncDisposable
{
    private const string Password = "copper kettle on the hob";
    private const string Peer = "198.51.100.23";
    private const string Agent = "BrightTimeAuditTest/1.0";

    private WebApplicationFactory<Program>? _app;

    /// <summary>The shared app, with a fixed client address standing in for the TCP peer.</summary>
    private WebApplicationFactory<Program> App => _app ??= db.Factory.WithWebHostBuilder(b =>
        b.ConfigureServices(s => s.AddSingleton<IStartupFilter, PeerFilter>()));

    private Browser NewBrowser()
    {
        HttpClient client = App.CreateClient(ConsoleFixture.ClientOptions());
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BrightTimeAuditTest", "1.0"));
        return new Browser(client);
    }

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Admin(string orgSlug) => $"admin@{orgSlug}.example.org";

    private static Guid UserOf(string email) => SeedIds.For("user/" + email);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_password_reset_and_password_sign_ins_are_recorded_against_the_account()
    {
        RequireDatabase();
        string email = Admin("alder-valley-community-schools");
        Guid user = UserOf(email);
        await SetPasswordAsync(email);

        (await WaitForCountAsync("auth.password_reset.requested", user)).ShouldBe(1, "the reset email was asked for");
        (await CountAsync("auth.password_reset.completed", user, "allow")).ShouldBe(1);

        using Browser browser = NewBrowser();
        await SignInWithPasswordAsync(browser, email, "not the password at all");
        (await CountAsync("auth.sign_in", user, "deny")).ShouldBe(1);
        (await db.ScalarAsync<string>(
            "select reason from platform.audit_event where action = 'auth.sign_in' and outcome = 'deny' and subject_ids = array[@u]",
            ("u", user))).ShouldBe(PasswordCheckOutcome.WrongPassword);

        (await SignInWithPasswordAsync(browser, email, Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
        (await db.ScalarAsync<long>(
            """
            select count(*) from platform.audit_event
            where action = 'auth.sign_in' and outcome = 'allow' and actor_id = @u and actor_kind = 'user'
              and subject_ids = array[@u] and detail->>'method' = 'password' and resource = 'POST /sign-in'
            """, ("u", user))).ShouldBe(1);
    }

    [Fact]
    public async Task A_sign_in_for_an_unknown_address_records_the_refusal_and_not_the_address()
    {
        RequireDatabase();
        const string typed = "nobody-at-all-audit@example.org";
        using Browser browser = NewBrowser();
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await SignInWithPasswordAsync(browser, typed, "whatever it is");

        (await db.ScalarAsync<long>(
            """
            select count(*) from platform.audit_event
            where action = 'auth.sign_in' and outcome = 'deny' and reason = 'no_account'
              and subject_ids is null and subject_count = 0 and actor_kind = 'anonymous' and occurred_at >= @t
            """, ("t", before))).ShouldBeGreaterThanOrEqualTo(1L);
        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where position(@e in detail::text || coalesce(reason, '') || coalesce(resource, '')) > 0",
            ("e", typed))).ShouldBe(0, "the typed address is stored nowhere");
    }

    [Fact]
    public async Task Client_address_and_user_agent_are_kept_only_as_keyed_hashes()
    {
        RequireDatabase();
        string email = Admin("kettleton-parks-and-recreation");
        using Browser browser = NewBrowser();
        await browser.SignInAsync(email);

        (byte[] ip, byte[] ua) = await HashesAsync(UserOf(email));

        ip.Length.ShouldBe(32);
        ua.Length.ShouldBe(32);
        ip.ShouldBe(Hmac("ip:" + Peer), "the same client hashes the same within a deployment");
        ua.ShouldBe(Hmac("ua:" + Agent));
        ip.ShouldNotBe(SHA256.HashData(Encoding.UTF8.GetBytes("ip:" + Peer)), "an unkeyed hash of an address is reversible by enumeration");
        Encoding.UTF8.GetString(ip).ShouldNotContain(Peer);
    }

    [Fact]
    public async Task Signing_out_is_recorded()
    {
        RequireDatabase();
        string email = Admin("quarry-hill-wexley-afterschool");
        using Browser browser = NewBrowser();
        await browser.SignInAsync(email);
        await browser.GetAsync("/provider");

        (await browser.PostAsync("/sign-out", null)).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        (await CountAsync("auth.sign_in", UserOf(email), "allow")).ShouldBe(1);
        (await CountAsync("auth.sign_out", UserOf(email), "allow")).ShouldBe(1);
    }

    [Fact]
    public async Task An_organiser_searches_the_trail_and_the_search_is_itself_recorded()
    {
        RequireDatabase();
        string provider = Admin("kettleton-city-schools-afterschool");
        using (Browser someone = NewBrowser())
        {
            await someone.SignInAsync(provider);
        }

        using Browser organiser = NewBrowser();
        await organiser.SignInAsync("organiser2@kettle.example.org");
        HttpResponseMessage page = await organiser.GetAsync("/organiser/audit?actor=" + Uri.EscapeDataString(provider) + "&action=auth.");

        page.StatusCode.ShouldBe(HttpStatusCode.OK, organiser.Html);
        organiser.Html.ShouldContain(provider);
        organiser.Html.ShouldContain("auth.sign_in");

        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'audit.searched' and actor_id = @a and detail->>'actor' = @p and detail->>'action' = 'auth.'",
            ("a", UserOf("organiser2@kettle.example.org")), ("p", UserOf(provider).ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_admin_holds_audit_read_but_not_for_the_whole_tenant()
    {
        RequireDatabase();
        using Browser provider = NewBrowser();
        await provider.SignInAsync(Admin("grayhaven-brannock-extended-day"));

        HttpResponseMessage page = await provider.GetAsync("/organiser/audit");

        page.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
        if (page.StatusCode == HttpStatusCode.Redirect)
        {
            page.Headers.Location!.ToString().ShouldContain("access-denied");
        }
    }

    [Fact]
    public async Task The_explorer_pages_backwards_by_keyset_without_repeating_a_row()
    {
        RequireDatabase();
        AuditLog log = App.Services.GetRequiredService<AuditLog>();
        TenantId tenant = new(KettleSeed.TenantId);
        UserId reader = new(KettleAccounts.OrganiserId);

        // Enough rows of one action to need two pages.
        string action = "test.paging." + Guid.NewGuid().ToString("N")[..8];
        await RecordAsync(Enumerable.Range(0, AuditLog.PageSize + 7).Select(_ => new AuditAccess { Action = action }));

        AuditPage first = await log.SearchAsync(tenant, reader, new AuditFilter { ActionPrefix = action }, null, TestContext.Current.CancellationToken);
        first.Rows.Count.ShouldBe(AuditLog.PageSize);
        first.Older.ShouldNotBeNull();

        AuditPage second = await log.SearchAsync(tenant, reader, new AuditFilter { ActionPrefix = action }, first.Older, TestContext.Current.CancellationToken);
        second.Rows.Count.ShouldBe(7);
        second.Older.ShouldBeNull();
        first.Rows.Select(r => r.Id).Intersect(second.Rows.Select(r => r.Id)).ShouldBeEmpty();
        first.Rows.Concat(second.Rows).Select(r => r.OccurredAt).ShouldBeInOrder(SortDirection.Descending);

        AuditLog.Decode("not-a-cursor").ShouldBeNull();
    }

    [Fact]
    public async Task An_endpoint_that_declares_youth_record_but_records_nothing_leaves_an_error_row()
    {
        RequireDatabase();
        string action = "test.youth_record." + Guid.NewGuid().ToString("N")[..8];

        await using ServiceProvider services = AuditServicesFor(out AuditTrail trail, out TenantContext tenant);
        DefaultHttpContext http = new();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new YouthRecordAttribute(action)), "test"));
        AuditFlushMiddleware middleware = new(_ => Task.CompletedTask, NullLogger<AuditFlushMiddleware>.Instance);

        await middleware.InvokeAsync(http, trail, tenant);

        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = @a and outcome = 'error' and reason like '%recorded no audit event%'",
            ("a", action))).ShouldBe(1);
    }

    // ---- helpers ----------------------------------------------------------------------

    private Task<long> CountAsync(string action, Guid user, string outcome) =>
        db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = @a and outcome = @o and subject_ids = array[@u]",
            ("a", action), ("o", outcome), ("u", user));

    /// <summary>The reset-requested row is written by the background dispatcher, after the mail.</summary>
    private async Task<long> WaitForCountAsync(string action, Guid user)
    {
        for (int i = 0; i < 50; i++)
        {
            long n = await CountAsync(action, user, "allow");
            if (n > 0)
            {
                return n;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return 0;
    }

    private async Task<(byte[] Ip, byte[] Ua)> HashesAsync(Guid user)
    {
        await using NpgsqlConnection connection = new(db.Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "select ip_hash, user_agent_hash from platform.audit_event where action = 'auth.sign_in' and actor_id = @u order by occurred_at desc limit 1",
            connection);
        command.Parameters.AddWithValue("u", user);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("the sign-in was recorded");
        return (
            await reader.GetFieldValueAsync<byte[]>(0, TestContext.Current.CancellationToken),
            await reader.GetFieldValueAsync<byte[]>(1, TestContext.Current.CancellationToken));
    }

    private static byte[] Hmac(string value) =>
        HMACSHA256.HashData(Convert.FromBase64String(ConsoleFixture.AuditHashKey), Encoding.UTF8.GetBytes(value));

    private async Task RecordAsync(IEnumerable<AuditAccess> events)
    {
        await using ServiceProvider services = AuditServicesFor(out AuditTrail trail, out _);
        foreach (AuditAccess e in events)
        {
            trail.Record(e);
        }

        await trail.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An audit trail on the web host's own login, for the Kettle tenant, outside any request.</summary>
    private ServiceProvider AuditServicesFor(out AuditTrail trail, out TenantContext tenant)
    {
        ServiceCollection services = new();
        string connection = db.WebLoginConnection;
        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        ServiceProvider provider = services.BuildServiceProvider();
        tenant = new TenantContext();
        tenant.SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.Default);
        trail = new AuditTrail(tenant, NoAuditRequestInfo.Instance, SystemClock.Instance, provider.GetRequiredService<NpgsqlDataSource>());
        return provider;
    }

    private async Task SetPasswordAsync(string email)
    {
        using Browser browser = NewBrowser();
        int seen = db.Mail.To(email).Count;
        await browser.GetAsync("/account/forgot");
        await browser.PostAsync("/account/forgot", "forgot", ("email", email));
        EmailMessage mail = (await db.Mail.WaitForAsync(email, seen)).ShouldNotBeNull();
        string link = LinkPattern().Match(mail.TextBody).Value;
        string path = new Uri(link).PathAndQuery;
        string token = Uri.UnescapeDataString(new Uri(link).Query.Split("token=")[1]);

        await browser.GetAsync(path);
        (await browser.PostAsync(path, "reset", ("token", token), ("password", Password), ("confirm", Password)))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect, browser.Html);
    }

    private static async Task<HttpResponseMessage> SignInWithPasswordAsync(Browser browser, string email, string password)
    {
        await browser.GetAsync("/sign-in");
        return await browser.PostAsync("/sign-in", "sign-in", ("email", email), ("password", password));
    }

    [GeneratedRegex(@"https?://\S+/account/reset\?token=\S+")]
    private static partial Regex LinkPattern();

    /// <summary>Stands in for the TCP connection, as a real client would have one.</summary>
    private sealed class PeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(Peer);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
