using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Localisation;

namespace BrightTime.Ui.Shared.Api;

/// <summary>
/// The app's view of <c>/api/v1</c> (docs/plan/05): one method per call it makes, each answering
/// a value or the problem that stopped it. The <see cref="HttpClient"/> is based at the region's
/// <c>/api/v1/</c>.
/// </summary>
/// <remarks>
/// Every request says the language the app is in, so problems, the inbox and taxonomy names come
/// back in it. A signed-in call that meets a 401 refreshes once and tries again; if the refresh
/// fails the person is signed out (<see cref="AppSession"/>).
/// </remarks>
public sealed class BrightTimeApi(HttpClient http, AppSession session, ITextContext language)
{
    // ---- public: the finder, as anyone sees it (D30) ------------------------------------------

    public Task<ApiResult<TenantInfo>> TenantAsync(CancellationToken ct = default) =>
        GetAsync<TenantInfo>("public/tenant", signedIn: false, ct);

    public Task<ApiResult<Page<ListingSummary>>> SearchAsync(SearchFilters filters, string? cursor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return GetAsync<Page<ListingSummary>>("public/listings?" + filters.ToQuery(cursor), signedIn: false, ct);
    }

    public Task<ApiResult<ListingDetail>> ListingAsync(string slug, CancellationToken ct = default) =>
        GetAsync<ListingDetail>("public/listings/" + Uri.EscapeDataString(slug), signedIn: false, ct);

    public Task<ApiResult<IReadOnlyList<NeighbourhoodSummary>>> NeighbourhoodsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<NeighbourhoodSummary>>("public/neighbourhoods", signedIn: false, ct);

    /// <summary>A vocabulary's terms (<c>category</c>, <c>interest</c>, <c>accommodation</c>), named in the app's language.</summary>
    public Task<ApiResult<IReadOnlyList<Term>>> TermsAsync(string vocabulary, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Term>>("public/taxonomy/" + Uri.EscapeDataString(vocabulary), signedIn: false, ct);

    // ---- accounts --------------------------------------------------------------------------------

    /// <summary>
    /// Registers an address (D34, D35). Answers the same for every address; the email that follows
    /// says what to do. <paramref name="birthMonth"/> is <c>yyyy-MM</c>; under 13 is refused.
    /// </summary>
    public Task<ApiResult> RegisterAsync(string email, string birthMonth, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "auth/register", new { email, birthMonth }, signedIn: false, ct);

    /// <summary>The external providers the region signs in with (D41).</summary>
    public Task<ApiResult<IReadOnlyList<ExternalProvider>>> ExternalProvidersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ExternalProvider>>("auth/external/providers", signedIn: false, ct);

    // ---- passkeys (D38) ----------------------------------------------------------------------

    /// <summary>Options to add a passkey. Needs a recent sign-in: an older session gets 403.</summary>
    public Task<ApiResult<PasskeyCeremony>> PasskeyRegistrationAsync(CancellationToken ct = default) =>
        SendAsync<PasskeyCeremony>(HttpMethod.Post, "auth/passkeys/options", null, signedIn: true, ct);

    /// <summary>The authenticator's answer to <see cref="PasskeyRegistrationAsync"/>, as the platform gave it.</summary>
    public Task<ApiResult> AddPasskeyAsync(string state, string credentialJson, string? name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "auth/passkeys", new { state, credential = JsonDocument.Parse(credentialJson).RootElement, name }, signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<PasskeyInfo>>> PasskeysAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PasskeyInfo>>("me/passkeys", signedIn: true, ct);

    public Task<ApiResult> RemovePasskeyAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"me/passkeys/{id:D}", null, signedIn: true, ct);

    /// <summary>The terms of use, which anyone may read (D37).</summary>
    public Task<ApiResult<TermsDocument>> TermsAsync(CancellationToken ct = default) =>
        GetAsync<TermsDocument>("terms", signedIn: false, ct);

    public Task<ApiResult<MyTerms>> MyTermsAsync(CancellationToken ct = default) =>
        GetAsync<MyTerms>("me/terms", signedIn: true, ct);

    /// <summary>Agrees to the terms the person was shown, by their hash; a stale hash is refused.</summary>
    public Task<ApiResult<MyTerms>> AcceptTermsAsync(string hash, CancellationToken ct = default) =>
        SendAsync<MyTerms>(HttpMethod.Post, "me/terms", new { hash }, signedIn: true, ct);

    /// <summary>Asks for a reset link. Answers the same for every address; the email says what to do.</summary>
    public Task<ApiResult> ForgotPasswordAsync(string email, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "auth/password/forgot", new { email }, signedIn: false, ct);

    /// <summary>Ends every session this person has, on every device.</summary>
    public Task<ApiResult> SignOutEverywhereAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "me/sessions", null, signedIn: true, ct);

    // ---- the person's own finder data (D33) --------------------------------------------------

    public Task<ApiResult<Page<ListingSummary>>> FeedAsync(string? cursor = null, CancellationToken ct = default) =>
        GetAsync<Page<ListingSummary>>("me/feed" + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)), signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<SavedItem>>> SavedAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SavedItem>>("me/saved-items", signedIn: true, ct);

    public Task<ApiResult> SaveAsync(Guid listingId, string? note = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"me/saved-items/{listingId:D}", new { note }, signedIn: true, ct);

    public Task<ApiResult> UnsaveAsync(Guid listingId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"me/saved-items/{listingId:D}", null, signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<Interest>>> InterestsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Interest>>("me/interests", signedIn: true, ct);

    public Task<ApiResult> SetInterestsAsync(IEnumerable<(string Code, int Weight)> interests, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, "me/interests", new { interests = interests.Select(i => new { code = i.Code, weight = i.Weight }).ToList() }, signedIn: true, ct);

    public Task<ApiResult<SearchPreferences>> SearchPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<SearchPreferences>("me/search-preferences", signedIn: true, ct);

    public Task<ApiResult> SetSearchPreferencesAsync(string? nearNeighbourhood, int? maxDistanceMetres, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, "me/search-preferences", new { nearNeighbourhood, maxDistanceMetres }, signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<Reminder>>> RemindersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Reminder>>("me/reminders", signedIn: true, ct);

    /// <summary>A reminder before the listing's next deadline (<c>deadline</c>) or session (<c>next_session</c>).</summary>
    public Task<ApiResult<Reminder>> RemindAsync(Guid listingId, string anchor, int leadMinutes, CancellationToken ct = default) =>
        SendAsync<Reminder>(HttpMethod.Post, "me/reminders", new { listingId, anchor, leadMinutes }, signedIn: true, ct);

    public Task<ApiResult> ForgetReminderAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"me/reminders/{id:D}", null, signedIn: true, ct);

    // ---- notifications (D33, D35) ------------------------------------------------------------

    public Task<ApiResult<IReadOnlyList<InboxItem>>> InboxAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<InboxItem>>("me/notifications", signedIn: true, ct);

    public Task<ApiResult> MarkReadAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"me/notifications/{id:D}/read", null, signedIn: true, ct);

    public Task<ApiResult<NotificationSettings>> NotificationSettingsAsync(CancellationToken ct = default) =>
        GetAsync<NotificationSettings>("me/notification-preferences", signedIn: true, ct);

    public Task<ApiResult<NotificationSettings>> SetNotificationSettingsAsync(NotificationSettings settings, CancellationToken ct = default) =>
        SendAsync<NotificationSettings>(HttpMethod.Put, "me/notification-preferences", settings, signedIn: true, ct);

    /// <summary>Registers this phone for pushes, in the language its words should come in.</summary>
    public Task<ApiResult<RegisteredDevice>> RegisterDeviceAsync(string platform, string token, string? appVersion, string? osVersion, CancellationToken ct = default) =>
        SendAsync<RegisteredDevice>(HttpMethod.Post, "devices", new { platform, token, appVersion, osVersion, locale = language.Locale.Code }, signedIn: true, ct);

    public Task<ApiResult> UnregisterDeviceAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"devices/{id:D}", null, signedIn: true, ct);

    // ---- the household (D43, D44, D65, D71) ----------------------------------------------------

    /// <summary>The household; a problem with code <c>family.no_profile</c> when it hasn't been saved yet.</summary>
    public Task<ApiResult<Household>> HouseholdAsync(CancellationToken ct = default) =>
        GetAsync<Household>("family-profile", signedIn: true, ct);

    public Task<ApiResult<Household>> SaveHouseholdAsync(short? householdSize, string? primaryLanguage, bool needsInterpreter, CancellationToken ct = default) =>
        SendAsync<Household>(HttpMethod.Put, "family-profile", new { householdSize, primaryLanguage, needsInterpreter }, signedIn: true, ct);

    public Task<ApiResult<HouseholdMember>> AddMemberAsync(MemberDraft member, CancellationToken ct = default) =>
        SendAsync<HouseholdMember>(HttpMethod.Post, "family-profile/members", member, signedIn: true, ct);

    public Task<ApiResult<HouseholdMember>> UpdateMemberAsync(Guid id, MemberDraft member, CancellationToken ct = default) =>
        SendAsync<HouseholdMember>(HttpMethod.Put, $"family-profile/members/{id:D}", member, signedIn: true, ct);

    public Task<ApiResult> RemoveMemberAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"family-profile/members/{id:D}", null, signedIn: true, ct);

    /// <summary>Replaces a member's support needs with these.</summary>
    public Task<ApiResult<HouseholdMember>> SetSupportNeedsAsync(Guid id, IReadOnlyList<SupportNeedDraft> needs, CancellationToken ct = default) =>
        SendAsync<HouseholdMember>(HttpMethod.Put, $"family-profile/members/{id:D}/support-needs", new { needs }, signedIn: true, ct);

    /// <summary>Links a young member to an account (the person's own, or a child's they are guardian of); null unlinks.</summary>
    public Task<ApiResult<HouseholdMember>> LinkMemberAccountAsync(Guid id, Guid? userId, CancellationToken ct = default) =>
        SendAsync<HouseholdMember>(HttpMethod.Put, $"family-profile/members/{id:D}/account", new { userId }, signedIn: true, ct);

    public Task<ApiResult<HouseholdContact>> AddContactAsync(ContactDraft contact, CancellationToken ct = default) =>
        SendAsync<HouseholdContact>(HttpMethod.Post, "family-profile/contacts", contact, signedIn: true, ct);

    public Task<ApiResult<HouseholdContact>> UpdateContactAsync(Guid id, ContactDraft contact, CancellationToken ct = default) =>
        SendAsync<HouseholdContact>(HttpMethod.Put, $"family-profile/contacts/{id:D}", contact, signedIn: true, ct);

    public Task<ApiResult> RemoveContactAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"family-profile/contacts/{id:D}", null, signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<HouseholdShare>>> SharesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<HouseholdShare>>("family-profile/shares", signedIn: true, ct);

    /// <summary>Ends a share at once: the organisation stops seeing what it covered.</summary>
    public Task<ApiResult> StopSharingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"family-profile/shares/{id:D}", null, signedIn: true, ct);

    // ---- applying (D46, D47, D72) -------------------------------------------------------------

    /// <summary>Who an activity is for, before applying.</summary>
    public Task<ApiResult<IReadOnlyList<EligibilityRule>>> EligibilityAsync(string slug, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<EligibilityRule>>("public/listings/" + Uri.EscapeDataString(slug) + "/eligibility", signedIn: false, ct);

    public Task<ApiResult<IReadOnlyList<ApplicationSummary>>> ApplicationsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ApplicationSummary>>("applications", signedIn: true, ct);

    /// <summary>
    /// Starts a draft. The same <paramref name="key"/> sent again returns the same draft, so a
    /// retry on a bad connection never makes a second one.
    /// </summary>
    public Task<ApiResult<FamilyApplication>> StartApplicationAsync(Guid listingId, Guid memberId, string key, CancellationToken ct = default) =>
        SendAsync<FamilyApplication>(HttpMethod.Post, "applications", new { listingId, memberId }, signedIn: true, key, ct);

    public Task<ApiResult<FamilyApplication>> ApplicationAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<FamilyApplication>($"applications/{id:D}", signedIn: true, ct);

    /// <summary>The questions the draft answers: the form version it began on, not necessarily the listing's latest.</summary>
    public Task<ApiResult<ApplicationForm>> ApplicationFormAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<ApplicationForm>($"applications/{id:D}/form", signedIn: true, ct);

    /// <summary>Saves some answers; a null answer clears that question.</summary>
    public Task<ApiResult<FamilyApplication>> SaveAnswersAsync(Guid id, IReadOnlyDictionary<string, object?> answers, CancellationToken ct = default) =>
        SendAsync<FamilyApplication>(HttpMethod.Patch, $"applications/{id:D}", new { answers }, signedIn: true, ct);

    /// <summary>Sends the application, sharing the young person's <paramref name="shareFields"/> with the organisation until <paramref name="shareUntil"/>.</summary>
    public Task<ApiResult<FamilyApplication>> SubmitApplicationAsync(Guid id, IReadOnlyList<string> shareFields, DateTimeOffset? shareUntil, CancellationToken ct = default) =>
        SendAsync<FamilyApplication>(HttpMethod.Post, $"applications/{id:D}/submit", new { shareFields, shareExpiresAt = shareUntil }, signedIn: true, ct);

    /// <summary>Withdraws a sent application, or throws a draft away.</summary>
    public Task<ApiResult> WithdrawApplicationAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"applications/{id:D}/withdraw", null, signedIn: true, ct);

    public Task<ApiResult<FamilyApplication>> TakeOfferAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<FamilyApplication>(HttpMethod.Post, $"applications/{id:D}/offer/accept", null, signedIn: true, ct);

    public Task<ApiResult> DeclineOfferAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"applications/{id:D}/offer/decline", null, signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<ApplicationWaiver>>> ApplicationWaiversAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ApplicationWaiver>>($"applications/{id:D}/waivers", signedIn: true, ct);

    /// <summary>Signs a waiver by typing a full name, against the text's hash: a changed waiver is refused.</summary>
    public Task<ApiResult> SignWaiverAsync(Guid id, Guid waiverId, string typedName, string hash, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"applications/{id:D}/waivers/{waiverId:D}/sign", new { typedName, hash }, signedIn: true, ct);

    // ---- guardians, children and consent (D31, D34, D35) -------------------------------------

    public Task<ApiResult<IReadOnlyList<Child>>> ChildrenAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Child>>("children", signedIn: true, ct);

    public Task<ApiResult<Child>> ChildAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<Child>($"children/{id:D}", signedIn: true, ct);

    /// <summary>Makes a child profile (under 13). The guardian's consent email follows.</summary>
    public Task<ApiResult<Child>> CreateChildAsync(string givenName, string birthMonth, string relationship, CancellationToken ct = default) =>
        SendAsync<Child>(HttpMethod.Post, "children", new { givenName, birthMonth, relationship }, signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<Disclosure>>> DisclosuresAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Disclosure>>("consent/disclosures", signedIn: true, ct);

    /// <summary>Grants one scope, naming the hash of the disclosure the guardian was shown.</summary>
    public Task<ApiResult<GivenConsent>> GrantAsync(Guid child, string scope, string disclosureHash, CancellationToken ct = default) =>
        SendAsync<GivenConsent>(HttpMethod.Post, $"children/{child:D}/consents", new { scope, disclosureHash }, signedIn: true, ct);

    public Task<ApiResult> RevokeAsync(Guid child, string scope, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"children/{child:D}/consents/{Uri.EscapeDataString(scope)}", null, signedIn: true, ct);

    /// <summary>The guardian's answer to their consent email (email plus): the token from its link.</summary>
    public Task<ApiResult<Child>> VerifyLinkAsync(Guid link, string token, CancellationToken ct = default) =>
        SendAsync<Child>(HttpMethod.Post, $"guardian-links/{link:D}/verify", new { token }, signedIn: true, ct);

    public Task<ApiResult> ResendConsentEmailAsync(Guid link, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"guardian-links/{link:D}/consent-email", null, signedIn: true, ct);

    public Task<ApiResult> RevokeLinkAsync(Guid link, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"guardian-links/{link:D}/revoke", null, signedIn: true, ct);

    /// <summary>From 13, hands the profile to its owner at their own address (D35).</summary>
    public Task<ApiResult> ClaimAsync(Guid child, string email, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"children/{child:D}/claim", new { email }, signedIn: true, ct);

    // ---- plumbing ------------------------------------------------------------------------------

    // ---- attendance (D49, D51, D52) ----------------------------------------------------------

    /// <summary>The organisation's meetings around today, for a staff member to pick a roster.</summary>
    public Task<ApiResult<IReadOnlyList<AttendanceMeeting>>> MeetingsAsync(Guid org, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AttendanceMeeting>>($"org/{org:D}/attendance/meetings", signedIn: true, ct);

    public Task<ApiResult<AttendanceRoster>> RosterAsync(Guid org, Guid listing, DateTimeOffset occurrence, CancellationToken ct = default) =>
        GetAsync<AttendanceRoster>($"org/{org:D}/listings/{listing:D}/roster?occurrence={Uri.EscapeDataString(occurrence.ToString("O", CultureInfo.InvariantCulture))}", signedIn: true, ct);

    public Task<ApiResult<IReadOnlyList<AttendanceSyncOutcome>>> SyncAttendanceAsync(Guid org, string deviceId, string? appVersion, IReadOnlyList<AttendanceSyncItem> items, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<AttendanceSyncOutcome>>(HttpMethod.Post, $"org/{org:D}/attendance/batch", new { deviceId, appVersion, items }, signedIn: true, ct);

    /// <summary>Registers this phone with an organisation so it may sync (D51).</summary>
    public Task<ApiResult> RegisterAttendanceDeviceAsync(Guid org, string deviceId, string label, string? appVersion, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"org/{org:D}/kiosk-devices", new { deviceId, label, appVersion }, signedIn: true, ct);

    /// <summary>Asks whether to wipe. Works signed out too: a phone whose user has left must still learn it.</summary>
    public Task<ApiResult<AttendanceDeviceContact>> DeviceContactAsync(string deviceId, string? appVersion, CancellationToken ct = default) =>
        SendAsync<AttendanceDeviceContact>(HttpMethod.Post, "attendance/devices/contact", new { deviceId, appVersion }, signedIn: session.IsSignedIn, ct);

    private async Task<ApiResult<T>> GetAsync<T>(string path, bool signedIn, CancellationToken ct) =>
        await SendAsync<T>(HttpMethod.Get, path, null, signedIn, ct).ConfigureAwait(false);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body, bool signedIn, CancellationToken ct)
    {
        ApiResult<JsonElement> result = await SendAsync<JsonElement>(method, path, body, signedIn, ct).ConfigureAwait(false);
        return result.IsSuccess ? ApiResult.Success : ApiResult.Failure(result.Problem!);
    }

    private Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool signedIn, CancellationToken ct) =>
        SendAsync<T>(method, path, body, signedIn, idempotencyKey: null, ct);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool signedIn, string? idempotencyKey, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage first = await SendOnceAsync(method, path, body, signedIn, idempotencyKey, ct).ConfigureAwait(false);
            if (first.StatusCode != HttpStatusCode.Unauthorized || !signedIn)
            {
                return await ReadAsync<T>(first, ct).ConfigureAwait(false);
            }

            // The access token died early (signed out elsewhere, or a clock skew): refresh once.
            if (!await session.RefreshAsync(ct).ConfigureAwait(false))
            {
                return await ReadAsync<T>(first, ct).ConfigureAwait(false);
            }

            using HttpResponseMessage second = await SendOnceAsync(method, path, body, signedIn, idempotencyKey, ct).ConfigureAwait(false);
            return await ReadAsync<T>(second, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Failure(ApiProblem.Offline);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout: as good as offline.
            return ApiResult<T>.Failure(ApiProblem.Offline);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool signedIn, string? idempotencyKey, CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language.Locale.Code));
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        if (signedIn)
        {
            if (await session.AccessTokenAsync(ct).ConfigureAwait(false) is not { } token)
            {
                // Still signed in without a token: the refresh didn't reach the server (a refused one signs out).
                return session.IsSignedIn ? throw new HttpRequestException("The token refresh did not reach the server.") : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: AppSession.Json);
        }

        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            return ApiResult<T>.Failure(await ProblemAsync(response, ct).ConfigureAwait(false));
        }

        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
        {
            return ApiResult<T>.Success(default!);
        }

        try
        {
            T? value = await response.Content.ReadFromJsonAsync<T>(AppSession.Json, ct).ConfigureAwait(false);
            return value is null
                ? ApiResult<T>.Failure(new ApiProblem(ApiProblem.UnreadableType, null, (int)response.StatusCode))
                : ApiResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Failure(new ApiProblem(ApiProblem.UnreadableType, null, (int)response.StatusCode));
        }
    }

    /// <summary>Reads an RFC 9457 problem; anything else becomes a problem named by its status.</summary>
    internal static async Task<ApiProblem> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        int status = (int)response.StatusCode;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
            {
                using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                JsonElement root = doc.RootElement;
                string type = root.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : ByStatus(status);
                string? title = root.TryGetProperty("title", out JsonElement ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : null;
                Dictionary<string, string[]>? errors = null;
                if (root.TryGetProperty("errors", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
                {
                    errors = e.EnumerateObject().ToDictionary(
                        p => p.Name,
                        p => p.Value.ValueKind == JsonValueKind.Array ? [.. p.Value.EnumerateArray().Select(v => v.ToString())] : new[] { p.Value.ToString() },
                        StringComparer.Ordinal);
                }

                return new ApiProblem(type, title, status, errors);
            }
        }
        catch (JsonException)
        {
            // Not a problem document after all: fall through to the status.
        }

        return new ApiProblem(ByStatus(status), null, status);
    }

    private static string ByStatus(int status) => ApiProblem.Prefix + "http-" + status.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A phone registered for pushes.</summary>
public sealed record RegisteredDevice(Guid Id, string Platform, DateTimeOffset RegisteredAt);
