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

    private async Task<ApiResult<T>> GetAsync<T>(string path, bool signedIn, CancellationToken ct) =>
        await SendAsync<T>(HttpMethod.Get, path, null, signedIn, ct).ConfigureAwait(false);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body, bool signedIn, CancellationToken ct)
    {
        ApiResult<JsonElement> result = await SendAsync<JsonElement>(method, path, body, signedIn, ct).ConfigureAwait(false);
        return result.IsSuccess ? ApiResult.Success : ApiResult.Failure(result.Problem!);
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool signedIn, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage first = await SendOnceAsync(method, path, body, signedIn, ct).ConfigureAwait(false);
            if (first.StatusCode != HttpStatusCode.Unauthorized || !signedIn)
            {
                return await ReadAsync<T>(first, ct).ConfigureAwait(false);
            }

            // The access token died early (signed out elsewhere, or a clock skew): refresh once.
            if (!await session.RefreshAsync(ct).ConfigureAwait(false))
            {
                return await ReadAsync<T>(first, ct).ConfigureAwait(false);
            }

            using HttpResponseMessage second = await SendOnceAsync(method, path, body, signedIn, ct).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool signedIn, CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language.Locale.Code));
        if (signedIn)
        {
            if (await session.AccessTokenAsync(ct).ConfigureAwait(false) is not { } token)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
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
