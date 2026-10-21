using System.Globalization;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Ui.Shared.Attendance;

/// <summary>A roster taken onto the phone: the minimum (docs/plan/06), the enrolment and a display name, for one meeting.</summary>
public sealed record RosterPack(Guid OrgId, Guid ListingId, string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<PackLine> Lines, DateTimeOffset DownloadedAt);

public sealed record PackLine(Guid EnrollmentId, string? DisplayName);

/// <summary>A mark made on the phone and not yet accepted by the server.</summary>
public sealed record QueuedMark(Guid ClientRecordId, Guid OrgId, Guid ListingId, Guid EnrollmentId, DateTimeOffset StartsAt, string State, DateTimeOffset CapturedAt);

/// <summary>A mark the server didn't take: someone recorded it first (conflict), or it was refused, with why.</summary>
public sealed record SyncProblem(Guid EnrollmentId, string? DisplayName, DateTimeOffset StartsAt, string Status, string? Reason);

/// <summary>Whether the roster may be opened (docs/plan/07, T12).</summary>
public enum RosterGate
{
    Open,

    /// <summary>No contact with the server for longer than allowed: it must reach the server first.</summary>
    Locked,

    /// <summary>The device was revoked and has just wiped everything it held.</summary>
    Wiped,
}

/// <summary>What a sync did. <see cref="NeedsRegistration"/> names organisations this phone must register with first.</summary>
public sealed record SyncSummary(int Accepted, int Problems, int Pending, bool Offline, bool Wiped, IReadOnlyList<Guid> NeedsRegistration);

/// <summary>
/// The staff roster on the phone (M3b, D52): taken onto the device for a meeting, marked with or
/// without a signal, and synced when the signal returns.
/// </summary>
/// <remarks>
/// <para>
/// Everything that names a child is in the <see cref="SealedStore"/>: the packs, the queue of
/// marks, and the problems. Plain preferences hold only when the server was last reached and
/// how long the phone may go without it.
/// </para>
/// <para>
/// Sync is D49's batch, under a device id of its own (not the sign-in one), registered with each
/// organisation (D51). Every mark carries a fresh client id, so a batch whose answer was lost is
/// sent again safely: the server answers <c>duplicate</c>.
/// </para>
/// <para>
/// The server can say wipe, on any contact or by refusing a batch as revoked. Then every pack,
/// mark and the key are gone, and the device takes a new id. Without contact for longer than
/// the server's limit (72 hours), the roster refuses to open until it reaches the server.
/// </para>
/// </remarks>
public sealed class OfflineRoster(BrightTimeApi api, SealedStore store, ISecretStore secrets, IAppPreferences preferences, TimeProvider time)
{
    public const string DeviceIdKey = "brighttime.attendance_device";
    public const string LastContactKey = "brighttime.roster.last_contact";
    public const string MaxOfflineKey = "brighttime.roster.max_offline_hours";

    private const string PacksDocument = "roster.packs";
    private const string QueueDocument = "roster.queue";
    private const string ProblemsDocument = "roster.problems";

    /// <summary>How long a pack is kept after its meeting ends, when nothing for it is waiting to sync.</summary>
    public static readonly TimeSpan PackLifetime = TimeSpan.FromHours(24);

    public static readonly string? AppVersion = typeof(OfflineRoster).Assembly.GetName().Version?.ToString();

    /// <summary>
    /// Checks in with the server and decides whether the roster may open: wiped if the server
    /// says so, locked if it can't be reached and the last contact is too old. Also lets go of
    /// packs past their lifetime.
    /// </summary>
    public async Task<RosterGate> OpenAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<AttendanceDeviceContact> contact = await api.DeviceContactAsync(await DeviceIdAsync().ConfigureAwait(false), AppVersion, cancellationToken).ConfigureAwait(false);
        if (contact.Value is { } answer)
        {
            if (answer.Wipe)
            {
                await WipeAsync().ConfigureAwait(false);
                return RosterGate.Wiped;
            }

            preferences.Set(LastContactKey, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            preferences.Set(MaxOfflineKey, answer.MaxOfflineHours.ToString(CultureInfo.InvariantCulture));
        }
        else if (IsTooLongOffline())
        {
            return RosterGate.Locked;
        }

        await ForgetExpiredAsync().ConfigureAwait(false);
        return RosterGate.Open;
    }

    /// <summary>Registers this phone with an organisation, so its marks may sync there.</summary>
    public async Task<ApiResult> RegisterAsync(Guid org, string label, CancellationToken cancellationToken = default) =>
        await api.RegisterAttendanceDeviceAsync(org, await DeviceIdAsync().ConfigureAwait(false), label, AppVersion, cancellationToken).ConfigureAwait(false);

    /// <summary>Takes a meeting's roster onto the phone, replacing any earlier copy of the same meeting.</summary>
    public async Task<ApiResult<RosterPack>> DownloadAsync(Guid org, AttendanceMeeting meeting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ApiResult<AttendanceRoster> roster = await api.RosterAsync(org, meeting.ListingId, meeting.StartsAt, cancellationToken).ConfigureAwait(false);
        if (roster.Value is not { } found)
        {
            return ApiResult<RosterPack>.Failure(roster.Problem!);
        }

        RosterPack pack = new(org, meeting.ListingId, meeting.Title, found.OccurrenceStartsAt, found.OccurrenceEndsAt,
            [.. found.Lines.Select(l => new PackLine(l.EnrollmentId, l.DisplayName))], time.GetUtcNow());
        List<RosterPack> packs = [.. (await PacksAsync().ConfigureAwait(false)).Where(p => !Same(p, pack))];
        packs.Add(pack);
        await store.SaveAsync(PacksDocument, packs).ConfigureAwait(false);
        return ApiResult<RosterPack>.Success(pack);
    }

    public async Task<IReadOnlyList<RosterPack>> PacksAsync() =>
        await store.LoadAsync<List<RosterPack>>(PacksDocument).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<QueuedMark>> QueueAsync() =>
        await store.LoadAsync<List<QueuedMark>>(QueueDocument).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<SyncProblem>> ProblemsAsync() =>
        await store.LoadAsync<List<SyncProblem>>(ProblemsDocument).ConfigureAwait(false) ?? [];

    /// <summary>
    /// Marks a young person for a pack's meeting. A change before syncing replaces the earlier
    /// mark, under a new client id: an id once sent is never reused for different words.
    /// </summary>
    public async Task MarkAsync(RosterPack pack, Guid enrollmentId, string state)
    {
        ArgumentNullException.ThrowIfNull(pack);
        List<QueuedMark> queue = [.. (await QueueAsync().ConfigureAwait(false)).Where(m => !(m.EnrollmentId == enrollmentId && m.StartsAt == pack.StartsAt))];
        queue.Add(new QueuedMark(Guid.NewGuid(), pack.OrgId, pack.ListingId, enrollmentId, pack.StartsAt, state, time.GetUtcNow()));
        await store.SaveAsync(QueueDocument, queue).ConfigureAwait(false);
    }

    /// <summary>The marks waiting for a pack's meeting, by enrolment.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> MarksForAsync(RosterPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return (await QueueAsync().ConfigureAwait(false)).Where(m => m.StartsAt == pack.StartsAt && m.ListingId == pack.ListingId)
            .ToDictionary(m => m.EnrollmentId, m => m.State);
    }

    /// <summary>
    /// Sends the waiting marks, organisation by organisation. Accepted and duplicate marks leave the
    /// queue; conflicts and refusals leave it as problems to look at; anything unsent stays.
    /// </summary>
    public async Task<SyncSummary> SyncAsync(CancellationToken cancellationToken = default)
    {
        List<QueuedMark> queue = [.. await QueueAsync().ConfigureAwait(false)];
        List<SyncProblem> problems = [.. await ProblemsAsync().ConfigureAwait(false)];
        IReadOnlyList<RosterPack> packs = await PacksAsync().ConfigureAwait(false);
        string device = await DeviceIdAsync().ConfigureAwait(false);
        int accepted = 0;
        int problemCount = 0;
        bool offline = false;
        List<Guid> unregistered = [];

        foreach (IGrouping<Guid, QueuedMark> org in queue.GroupBy(m => m.OrgId).ToList())
        {
            List<AttendanceSyncItem> items = [.. org.Select(m => new AttendanceSyncItem(m.ClientRecordId, m.EnrollmentId, m.StartsAt, m.State, m.CapturedAt))];
            ApiResult<IReadOnlyList<AttendanceSyncOutcome>> sent = await api.SyncAttendanceAsync(org.Key, device, AppVersion, items, cancellationToken).ConfigureAwait(false);
            if (sent.Problem is { } problem)
            {
                switch (problem.Code)
                {
                    case "device.revoked":
                        await WipeAsync().ConfigureAwait(false);
                        return new SyncSummary(accepted, problemCount, 0, Offline: false, Wiped: true, []);
                    case "device.unknown":
                        unregistered.Add(org.Key);
                        break;
                    default:
                        offline |= problem.Type == ApiProblem.OfflineType;
                        break;
                }

                continue;
            }

            preferences.Set(LastContactKey, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            Dictionary<Guid, AttendanceSyncOutcome> outcomes = sent.Value!.ToDictionary(o => o.ClientRecordId);
            foreach (QueuedMark mark in org)
            {
                if (!outcomes.TryGetValue(mark.ClientRecordId, out AttendanceSyncOutcome? outcome))
                {
                    continue;
                }

                queue.Remove(mark);
                if (outcome.Status is "applied" or "duplicate")
                {
                    accepted++;
                }
                else
                {
                    problemCount++;
                    string? name = packs.FirstOrDefault(p => p.StartsAt == mark.StartsAt && p.ListingId == mark.ListingId)?
                        .Lines.FirstOrDefault(l => l.EnrollmentId == mark.EnrollmentId)?.DisplayName;
                    problems.Add(new SyncProblem(mark.EnrollmentId, name, mark.StartsAt, outcome.Status, outcome.Reason));
                }
            }
        }

        await store.SaveAsync(QueueDocument, queue).ConfigureAwait(false);
        await store.SaveAsync(ProblemsDocument, problems).ConfigureAwait(false);
        return new SyncSummary(accepted, problemCount, queue.Count, offline, Wiped: false, unregistered);
    }

    /// <summary>Clears the problems once the person has looked at them.</summary>
    public Task DismissProblemsAsync() => store.SaveAsync(ProblemsDocument, new List<SyncProblem>());

    /// <summary>
    /// Forgets everything: the key (so nothing left behind can be read), every pack, mark and
    /// problem, and the device id, so a fresh one registers next time.
    /// </summary>
    public async Task WipeAsync()
    {
        await store.WipeAsync().ConfigureAwait(false);
        await secrets.RemoveAsync(DeviceIdKey).ConfigureAwait(false);
        preferences.Set(LastContactKey, null);
    }

    /// <summary>True when the server was last reached longer ago than it allows (or never, with packs on the phone).</summary>
    public bool IsTooLongOffline()
    {
        int hours = int.TryParse(preferences.Get(MaxOfflineKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int set) ? set : 72;
        return DateTimeOffset.TryParse(preferences.Get(LastContactKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset last)
            && time.GetUtcNow() - last > TimeSpan.FromHours(hours);
    }

    private async Task ForgetExpiredAsync()
    {
        IReadOnlyList<RosterPack> packs = await PacksAsync().ConfigureAwait(false);
        IReadOnlyList<QueuedMark> queue = await QueueAsync().ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        List<RosterPack> kept = [.. packs.Where(p => p.EndsAt + PackLifetime > now || queue.Any(m => m.ListingId == p.ListingId && m.StartsAt == p.StartsAt))];
        if (kept.Count != packs.Count)
        {
            await store.SaveAsync(PacksDocument, kept).ConfigureAwait(false);
        }
    }

    private async Task<string> DeviceIdAsync()
    {
        if (await secrets.GetAsync(DeviceIdKey).ConfigureAwait(false) is { Length: > 0 } known)
        {
            return known;
        }

        string id = "app-" + Guid.NewGuid().ToString("N");
        await secrets.SetAsync(DeviceIdKey, id).ConfigureAwait(false);
        return id;
    }

    private static bool Same(RosterPack a, RosterPack b) => a.OrgId == b.OrgId && a.ListingId == b.ListingId && a.StartsAt == b.StartsAt;
}
