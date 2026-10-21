using System.Net;
using System.Text;
using System.Text.Json;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Attendance;
using BrightTime.Ui.Shared.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Ui.Shared.Tests;

/// <summary>
/// The staff roster on the phone (D52): sealed at rest, marked offline, synced once, and wiped or
/// locked as the server and the clock say.
/// </summary>
public sealed class OfflineRosterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Org = Guid.Parse("0a0a0000-0000-0000-0000-00000000000a");
    private static readonly Guid Listing = Guid.Parse("1b1b0000-0000-0000-0000-00000000001b");
    private static readonly Guid Zara = Guid.Parse("2c2c0000-0000-0000-0000-0000000000a1");
    private static readonly Guid Yusuf = Guid.Parse("2c2c0000-0000-0000-0000-0000000000a2");
    private static readonly Guid Amani = Guid.Parse("2c2c0000-0000-0000-0000-0000000000a3");
    private static readonly DateTimeOffset Meeting = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    /// <summary>A clock the test moves.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Meeting.AddMinutes(10);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Server
    {
        public bool Wipe { get; set; }

        /// <summary>What the batch answers: an outcome per enrolment (applied when not named), or a problem code for the whole batch.</summary>
        public Dictionary<Guid, string> Outcomes { get; } = [];

        public string? BatchProblem { get; set; }

        public List<JsonElement> Batches { get; } = [];
    }

    private static (FakeApi Api, Server Server) Fake()
    {
        Server server = new();
        FakeApi api = new FakeApi()
            .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
            .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
            .Json(HttpMethod.Post, "auth/token/refresh", FakeApi.Renewed)
            .Json(HttpMethod.Get, "me", FakeApi.Me("adult"))
            .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
            .On(HttpMethod.Post, "attendance/devices/contact", _ => FakeApi.Respond(HttpStatusCode.OK, $$"""{"registered":true,"wipe":{{(server.Wipe ? "true" : "false")}},"maxOfflineHours":72}"""))
            .Json(HttpMethod.Get, $"org/{Org}/listings/{Listing}/roster", $$"""
                {"listingId":"{{Listing}}","occurrenceStartsAt":"{{Meeting:O}}","occurrenceEndsAt":"{{Meeting.AddHours(1):O}}",
                 "lines":[{"enrollmentId":"{{Zara}}","displayName":"Zara K.","recordId":null,"state":null},
                          {"enrollmentId":"{{Yusuf}}","displayName":"Yusuf K.","recordId":null,"state":null},
                          {"enrollmentId":"{{Amani}}","displayName":null,"recordId":null,"state":null}]}
                """)
            .On(HttpMethod.Post, $"org/{Org}/kiosk-devices", _ => FakeApi.Respond(HttpStatusCode.Created, "{}"))
            .On(HttpMethod.Post, $"org/{Org}/attendance/batch", request =>
            {
                JsonElement body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.Clone();
                server.Batches.Add(body);
                if (server.BatchProblem is { } code)
                {
                    return FakeApi.Problem(HttpStatusCode.Forbidden, code);
                }

                IEnumerable<string> outcomes = body.GetProperty("items").EnumerateArray().Select(i =>
                {
                    Guid enrolment = i.GetProperty("enrollmentId").GetGuid();
                    string status = server.Outcomes.GetValueOrDefault(enrolment, "applied");
                    string reason = status == "rejected" ? "\"attendance.too_late\"" : "null";
                    return $$"""{"clientRecordId":"{{i.GetProperty("clientRecordId").GetGuid()}}","status":"{{status}}","serverId":null,"reason":{{reason}}}""";
                });
                return FakeApi.Respond(HttpStatusCode.OK, "[" + string.Join(',', outcomes) + "]");
            });
        return (api, server);
    }

    private static async Task<(ServiceProvider App, OfflineRoster Roster, Clock Clock)> StartAsync(FakeApi api)
    {
        Clock clock = new();
        ServiceProvider app = api.Services(s => s.AddSingleton<TimeProvider>(clock));
        (await app.GetRequiredService<AppSession>().SignInAsync("staff@example.net", "pw", Ct)).ShouldBeNull();
        return (app, app.GetRequiredService<OfflineRoster>(), clock);
    }

    private static AttendanceMeeting Today => new(Listing, "Robotics Club", Meeting, Meeting.AddHours(1));

    [Fact]
    public async Task The_roster_and_its_marks_are_sealed_on_the_phone_and_a_wipe_leaves_nothing_readable()
    {
        (FakeApi api, _) = Fake();
        using (api)
        {
            (ServiceProvider app, OfflineRoster roster, _) = await StartAsync(api);
            await using (app)
            {
                (await roster.OpenAsync(Ct)).ShouldBe(RosterGate.Open);
                RosterPack pack = (await roster.DownloadAsync(Org, Today, Ct)).Value!;
                pack.Lines.Select(l => l.DisplayName).ShouldBe(["Zara K.", "Yusuf K.", null]);
                await roster.MarkAsync(pack, Zara, "present");

                // Only ciphertext in storage: no name, no enrolment id, in any blob.
                MemoryBlobStore blobs = (MemoryBlobStore)app.GetRequiredService<IBlobStore>();
                blobs.Blobs.Count.ShouldBe(2);
                foreach (byte[] blob in blobs.Blobs.Values)
                {
                    string raw = Encoding.UTF8.GetString(blob);
                    raw.ShouldNotContain("Zara");
                    raw.ShouldNotContain(Zara.ToString());
                }

                (await roster.PacksAsync()).Single().Title.ShouldBe("Robotics Club");
                (await roster.MarksForAsync(pack))[Zara].ShouldBe("present");

                // Wiped: the key goes first, then the blobs, and nothing reads back.
                await roster.WipeAsync();
                (await app.GetRequiredService<ISecretStore>().GetAsync(SealedStore.KeyName)).ShouldBeNull();
                blobs.Blobs.ShouldBeEmpty();
                (await roster.PacksAsync()).ShouldBeEmpty();
                (await roster.QueueAsync()).ShouldBeEmpty();
            }
        }
    }

    [Fact]
    public async Task Marks_are_sent_once_and_what_the_server_refused_is_kept_to_look_at()
    {
        (FakeApi api, Server server) = Fake();
        using (api)
        {
            (ServiceProvider app, OfflineRoster roster, _) = await StartAsync(api);
            await using (app)
            {
                await roster.OpenAsync(Ct);
                RosterPack pack = (await roster.DownloadAsync(Org, Today, Ct)).Value!;

                // A changed mark replaces the earlier one, under a new id.
                await roster.MarkAsync(pack, Zara, "present");
                Guid first = (await roster.QueueAsync()).Single().ClientRecordId;
                await roster.MarkAsync(pack, Zara, "late");
                QueuedMark changed = (await roster.QueueAsync()).Single();
                (changed.State, changed.ClientRecordId == first).ShouldBe(("late", false));
                await roster.MarkAsync(pack, Yusuf, "present");
                await roster.MarkAsync(pack, Amani, "absent");

                // No signal: nothing is lost.
                api.Offline = true;
                SyncSummary offline = await roster.SyncAsync(Ct);
                (offline.Offline, offline.Pending).ShouldBe((true, 3));
                api.Offline = false;

                // Not registered yet: kept, and the organisation is named so the screen can offer to register.
                server.BatchProblem = "device.unknown";
                SyncSummary unregistered = await roster.SyncAsync(Ct);
                unregistered.NeedsRegistration.ShouldBe([Org]);
                unregistered.Pending.ShouldBe(3);
                (await roster.RegisterAsync(Org, "Front desk phone", Ct)).IsSuccess.ShouldBeTrue();
                server.BatchProblem = null;

                // Sent: applied and duplicate are done; a conflict waits to be looked at.
                server.Outcomes[Yusuf] = "duplicate";
                server.Outcomes[Amani] = "conflict";
                SyncSummary sent = await roster.SyncAsync(Ct);
                (sent.Accepted, sent.Problems, sent.Pending).ShouldBe((2, 1, 0));
                SyncProblem problem = (await roster.ProblemsAsync()).Single();
                (problem.EnrollmentId, problem.Status, problem.DisplayName).ShouldBe((Amani, "conflict", null));
                await roster.DismissProblemsAsync();
                (await roster.ProblemsAsync()).ShouldBeEmpty();

                // The same device id throughout, never the sign-in one.
                string[] devices = [.. server.Batches.Select(b => b.GetProperty("deviceId").GetString()!).Distinct()];
                devices.ShouldHaveSingleItem().ShouldStartWith("app-");
            }
        }
    }

    [Fact]
    public async Task A_revoked_phone_wipes_itself_and_a_long_silence_locks_the_roster()
    {
        (FakeApi api, Server server) = Fake();
        using (api)
        {
            (ServiceProvider app, OfflineRoster roster, Clock clock) = await StartAsync(api);
            await using (app)
            {
                (await roster.OpenAsync(Ct)).ShouldBe(RosterGate.Open);
                RosterPack pack = (await roster.DownloadAsync(Org, Today, Ct)).Value!;
                await roster.MarkAsync(pack, Zara, "present");

                // Out of signal for ten hours, it opens; for three days and more, it doesn't.
                api.Offline = true;
                clock.Now = clock.Now.AddHours(10);
                (await roster.OpenAsync(Ct)).ShouldBe(RosterGate.Open);
                clock.Now = clock.Now.AddHours(63);
                (await roster.OpenAsync(Ct)).ShouldBe(RosterGate.Locked);
                (await roster.QueueAsync()).Count.ShouldBe(1, "locked is not wiped: the marks wait for a signal");

                // Back in signal, revoked: the batch is refused, and the phone wipes.
                api.Offline = false;
                server.BatchProblem = "device.revoked";
                SyncSummary refused = await roster.SyncAsync(Ct);
                refused.Wiped.ShouldBeTrue();
                (await roster.PacksAsync()).ShouldBeEmpty();
                (await app.GetRequiredService<ISecretStore>().GetAsync(OfflineRoster.DeviceIdKey)).ShouldBeNull();

                // Or told on contact.
                server.BatchProblem = null;
                await roster.DownloadAsync(Org, Today, Ct);
                server.Wipe = true;
                (await roster.OpenAsync(Ct)).ShouldBe(RosterGate.Wiped);
                (await roster.PacksAsync()).ShouldBeEmpty();
            }
        }
    }

    [Fact]
    public async Task A_pack_is_let_go_a_day_after_its_meeting_unless_marks_for_it_still_wait()
    {
        (FakeApi api, _) = Fake();
        using (api)
        {
            (ServiceProvider app, OfflineRoster roster, Clock clock) = await StartAsync(api);
            await using (app)
            {
                await roster.OpenAsync(Ct);
                RosterPack pack = (await roster.DownloadAsync(Org, Today, Ct)).Value!;
                await roster.MarkAsync(pack, Zara, "present");

                clock.Now = Meeting.AddDays(2);
                await roster.OpenAsync(Ct);
                (await roster.PacksAsync()).Count.ShouldBe(1, "a mark for it still waits");

                await roster.SyncAsync(Ct);
                await roster.OpenAsync(Ct);
                (await roster.PacksAsync()).ShouldBeEmpty();
            }
        }
    }
}
