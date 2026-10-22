using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrightTime.Abstractions.Payments;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrightTime.Modules.Enrollment.Payments;

public sealed class StripeOptions
{
    public const string SectionName = "Payments:Stripe";

    /// <summary>The secret API key (<c>sk_live_…</c> or <c>sk_test_…</c>). A secret: from the deployment's secret store.</summary>
    public string? SecretKey { get; set; }

    /// <summary>The webhook endpoint's signing secret (<c>whsec_…</c>). A secret too.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The API, overridable for a stand-in in tests.</summary>
    public Uri ApiBase { get; set; } = new("https://api.stripe.com/");

    /// <summary>The API version every request pins, so Stripe changing its default changes nothing here.</summary>
    public string ApiVersion { get; set; } = "2024-06-20";

    /// <summary>How old a signed webhook may be: Stripe's own recommended tolerance.</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(WebhookSecret);
}

/// <summary>
/// Stripe (Q6, D56), over its HTTP API: payment intents, refunds, and signed webhooks.
/// </summary>
/// <remarks>
/// <para>
/// The card is entered in Stripe's own element with the intent's client secret, so nothing
/// card-shaped reaches this application (T20, SAQ-A). Each create carries the caller's
/// idempotency key, so a retried request makes no second charge.
/// </para>
/// <para>
/// A webhook is trusted only with a valid <c>Stripe-Signature</c>: an HMAC-SHA256 of
/// <c>{timestamp}.{body}</c> under the endpoint's secret, compared in fixed time, no older than the
/// tolerance (so a captured callback can't be replayed later).
/// </para>
/// </remarks>
public sealed class StripePaymentGateway(HttpClient http, StripeOptions options, TimeProvider time) : IPaymentGateway
{
    public const string Name = "stripe";

    public string ProviderName => Name;

    public string WebhookSignatureHeader => "Stripe-Signature";

    public async Task<PaymentIntentDescriptor> CreateIntentAsync(PaymentIntentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<KeyValuePair<string, string>> form =
        [
            new("amount", request.Amount.Cents.ToString(CultureInfo.InvariantCulture)),
            new("currency", request.Amount.Currency.ToLowerInvariant()),
            new("description", request.Description),
            new("automatic_payment_methods[enabled]", "true"),
        ];
        form.AddRange((request.Metadata ?? new Dictionary<string, string>()).Select(m => new KeyValuePair<string, string>($"metadata[{m.Key}]", m.Value)));
        using JsonDocument intent = await SendAsync(HttpMethod.Post, "v1/payment_intents", form, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        return Descriptor(intent.RootElement);
    }

    public async Task<PaymentIntentDescriptor> GetIntentAsync(string providerReference, CancellationToken cancellationToken = default)
    {
        using JsonDocument intent = await SendAsync(HttpMethod.Get, "v1/payment_intents/" + Uri.EscapeDataString(providerReference), null, null, cancellationToken).ConfigureAwait(false);
        return Descriptor(intent.RootElement);
    }

    public async Task<PaymentIntentDescriptor> RefundAsync(string providerReference, Money amount, string reason, CancellationToken cancellationToken = default)
    {
        using JsonDocument refund = await SendAsync(HttpMethod.Post, "v1/refunds",
        [
            new("payment_intent", providerReference),
            new("amount", amount.Cents.ToString(CultureInfo.InvariantCulture)),
            new("reason", "requested_by_customer"),
            new("metadata[reason]", reason),
        ], "refund-" + providerReference + "-" + amount.Cents.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return new PaymentIntentDescriptor(providerReference, PaymentIntentState.Refunded, amount, null, null);
    }

    public bool TryReadWebhook(ReadOnlySpan<byte> payload, string signatureHeader, out PaymentWebhookEvent? webhookEvent)
    {
        webhookEvent = null;
        if (!Verified(payload, signatureHeader))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.ToArray());
            JsonElement root = document.RootElement;
            string eventId = root.GetProperty("id").GetString() ?? "";
            string type = root.GetProperty("type").GetString() ?? "";
            JsonElement data = root.GetProperty("data").GetProperty("object");
            (string? reference, PaymentIntentState? state) = type switch
            {
                "payment_intent.succeeded" => (Text(data, "id"), PaymentIntentState.Succeeded),
                "payment_intent.processing" => (Text(data, "id"), PaymentIntentState.Processing),
                "payment_intent.requires_action" => (Text(data, "id"), PaymentIntentState.RequiresAction),
                // A failed attempt leaves Stripe's intent waiting for another card, so the payment stays open.
                "payment_intent.payment_failed" => (Text(data, "id"), PaymentIntentState.RequiresAction),
                "payment_intent.canceled" => (Text(data, "id"), PaymentIntentState.Cancelled),
                "charge.refunded" when data.TryGetProperty("refunded", out JsonElement full) && full.ValueKind == JsonValueKind.True
                    => (Text(data, "payment_intent"), PaymentIntentState.Refunded),
                _ => ((string?)null, (PaymentIntentState?)null),
            };
            if (reference is { Length: > 0 } && state is { } now && eventId.Length > 0)
            {
                webhookEvent = new PaymentWebhookEvent(eventId, type, reference, now);
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Signed but unreadable: genuine, and nothing a payment can act on.
            return true;
        }
    }

    /// <summary>Stripe's scheme: <c>t=…,v1=…</c>, the HMAC of <c>{t}.{payload}</c>, within the tolerance.</summary>
    private bool Verified(ReadOnlySpan<byte> payload, string signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(options.WebhookSecret))
        {
            return false;
        }

        string? stamp = null;
        List<string> signatures = [];
        foreach (string part in signatureHeader.Split(','))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0].Trim() == "t")
            {
                stamp = pair[1].Trim();
            }
            else if (pair[0].Trim() == "v1")
            {
                signatures.Add(pair[1].Trim());
            }
        }

        if (stamp is null || !long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || signatures.Count == 0)
        {
            return false;
        }

        DateTimeOffset signedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if ((time.GetUtcNow() - signedAt).Duration() > options.WebhookTolerance)
        {
            return false;
        }

        byte[] signed = [.. Encoding.UTF8.GetBytes(stamp + "."), .. payload];
        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.WebhookSecret), signed);
        foreach (string signature in signatures)
        {
            if (signature.Length == expected.Length * 2 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), expected))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, IReadOnlyList<KeyValuePair<string, string>>? form, string? idempotencyKey, CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, new Uri(options.ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        request.Headers.Add("Stripe-Version", options.ApiVersion);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Stripe's error body names the problem, never the card; still, only its type and code go to logs.
                string kind = "unknown";
                try
                {
                    using JsonDocument error = JsonDocument.Parse(body);
                    kind = error.RootElement.GetProperty("error").TryGetProperty("code", out JsonElement code) ? code.GetString() ?? kind
                        : error.RootElement.GetProperty("error").GetProperty("type").GetString() ?? kind;
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                }

                throw new PaymentProviderException($"Stripe answered {(int)response.StatusCode} ({kind}) to {method} {path}.");
            }

            return JsonDocument.Parse(body);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderException($"Stripe could not be reached for {method} {path}.", ex);
        }
    }

    private static PaymentIntentDescriptor Descriptor(JsonElement intent) => new(
        intent.GetProperty("id").GetString()!,
        intent.GetProperty("status").GetString() switch
        {
            "succeeded" => PaymentIntentState.Succeeded,
            "processing" or "requires_capture" => PaymentIntentState.Processing,
            "canceled" => PaymentIntentState.Cancelled,
            _ => PaymentIntentState.RequiresAction,
        },
        Money.FromCents(intent.GetProperty("amount").GetInt32(), intent.GetProperty("currency").GetString()!.ToUpperInvariant()),
        Text(intent, "client_secret"),
        null);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public static class PaymentGatewayRegistration
{
    /// <summary>
    /// Stripe when <c>Payments:Stripe</c> holds a secret key and a webhook secret; otherwise the
    /// manual gateway (D55). Registered before <c>AddEnrollment</c>, which only fills the gap.
    /// </summary>
    public static IServiceCollection AddPaymentGateway(this IServiceCollection services, IConfiguration? configuration)
    {
        StripeOptions stripe = configuration?.GetSection(StripeOptions.SectionName).Get<StripeOptions>() ?? new();
        if (!stripe.IsConfigured)
        {
            services.TryAddSingleton<IPaymentGateway, ManualPaymentGateway>();
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(nameof(StripePaymentGateway), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IPaymentGateway>(sp => new StripePaymentGateway(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(StripePaymentGateway)), stripe, sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
