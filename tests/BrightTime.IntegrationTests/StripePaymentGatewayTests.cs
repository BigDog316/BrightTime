using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Abstractions.Payments;
using BrightTime.Modules.Enrollment.Payments;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The Stripe gateway (Q6, D56) against a stand-in for Stripe: what an intent is asked for with,
/// what Stripe's statuses mean here, what a refusal becomes, and which webhooks are believed.
/// </summary>
public sealed class StripePaymentGatewayTests
{
    private const string Secret = "whsec_test_secret";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeStripe : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Answer { get; set; } = """{"id":"pi_1","status":"requires_payment_method","amount":10000,"currency":"usd","client_secret":"pi_1_secret_2"}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static (StripePaymentGateway Gateway, FakeStripe Stripe, HttpClient Client) Gateway()
    {
        FakeStripe stripe = new();
        HttpClient client = new(stripe);
        return (new StripePaymentGateway(client, new StripeOptions { SecretKey = "sk_test_key", WebhookSecret = Secret }, new FixedTime(Now)), stripe, client);
    }

    private static string Sign(string body, DateTimeOffset at, string secret = Secret)
    {
        long t = at.ToUnixTimeSeconds();
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")))}";
    }

    private static string Event(string type, string data) => $$"""{"id":"evt_9","object":"event","type":"{{type}}","data":{"object":""" + data + "}}";

    [Fact]
    public async Task An_intent_is_asked_for_in_cents_with_the_key_the_version_and_the_callers_idempotency_key()
    {
        (StripePaymentGateway gateway, FakeStripe stripe, HttpClient client) = Gateway();
        using (client)
        {
            PaymentIntentDescriptor made = await gateway.CreateIntentAsync(new PaymentIntentRequest(Money.FromCents(10000, "USD"), "Summer session", "key-123",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "t1" }), Ct);

            made.ShouldBe(new PaymentIntentDescriptor("pi_1", PaymentIntentState.RequiresAction, Money.FromCents(10000, "USD"), "pi_1_secret_2", null));
            (HttpRequestMessage request, string body) = stripe.Seen.Single();
            (request.Method, request.RequestUri!.ToString()).ShouldBe((HttpMethod.Post, "https://api.stripe.com/v1/payment_intents"));
            request.Headers.Authorization!.ToString().ShouldBe("Bearer sk_test_key");
            request.Headers.GetValues("Stripe-Version").Single().ShouldBe("2024-06-20");
            request.Headers.GetValues("Idempotency-Key").Single().ShouldBe("key-123");
            body.Split('&').ShouldBe(["amount=10000", "currency=usd", "description=Summer+session", "automatic_payment_methods%5Benabled%5D=true", "metadata%5Btenant%5D=t1"]);
        }
    }

    [Theory]
    [InlineData("requires_payment_method", PaymentIntentState.RequiresAction)]
    [InlineData("requires_confirmation", PaymentIntentState.RequiresAction)]
    [InlineData("requires_action", PaymentIntentState.RequiresAction)]
    [InlineData("processing", PaymentIntentState.Processing)]
    [InlineData("requires_capture", PaymentIntentState.Processing)]
    [InlineData("succeeded", PaymentIntentState.Succeeded)]
    [InlineData("canceled", PaymentIntentState.Cancelled)]
    public async Task Stripes_statuses_map_to_ours(string stripeStatus, PaymentIntentState expected)
    {
        (StripePaymentGateway gateway, FakeStripe stripe, HttpClient client) = Gateway();
        using (client)
        {
            stripe.Answer = $$"""{"id":"pi_1","status":"{{stripeStatus}}","amount":500,"currency":"usd"}""";
            (await gateway.GetIntentAsync("pi_1", Ct)).State.ShouldBe(expected);
            stripe.Seen.Single().Request.RequestUri!.ToString().ShouldBe("https://api.stripe.com/v1/payment_intents/pi_1");
        }
    }

    [Fact]
    public async Task A_refusal_or_an_unreachable_stripe_is_a_provider_failure_naming_only_the_code()
    {
        (StripePaymentGateway gateway, FakeStripe stripe, HttpClient client) = Gateway();
        using (client)
        {
            stripe.Status = HttpStatusCode.PaymentRequired;
            stripe.Answer = """{"error":{"type":"card_error","code":"card_declined","message":"Your card was declined."}}""";
            PaymentProviderException refused = await Should.ThrowAsync<PaymentProviderException>(() => gateway.GetIntentAsync("pi_1", Ct));
            refused.Message.ShouldContain("card_declined");
            refused.Message.ShouldNotContain("Your card");
        }

        using Unreachable unreachable = new();
        using HttpClient down = new(unreachable, disposeHandler: false);
        await Should.ThrowAsync<PaymentProviderException>(() =>
            new StripePaymentGateway(down, new StripeOptions { SecretKey = "sk", WebhookSecret = Secret }, TimeProvider.System).GetIntentAsync("pi_1", Ct));
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host.");
    }

    [Fact]
    public void A_webhook_is_believed_only_when_signed_with_the_secret_recently_and_unaltered()
    {
        (StripePaymentGateway gateway, _, HttpClient client) = Gateway();
        using (client)
        {
            string body = Event("payment_intent.succeeded", """{"id":"pi_1","object":"payment_intent","status":"succeeded"}""");
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            gateway.TryReadWebhook(bytes, Sign(body, Now), out PaymentWebhookEvent? read).ShouldBeTrue();
            read.ShouldBe(new PaymentWebhookEvent("evt_9", "payment_intent.succeeded", "pi_1", PaymentIntentState.Succeeded));

            // A second signature (Stripe sends two while a secret is rolled) is enough if one matches.
            string rolled = Sign(body, Now, "whsec_old") + "," + Sign(body, Now).Split(',')[1];
            gateway.TryReadWebhook(bytes, rolled, out _).ShouldBeTrue();

            gateway.TryReadWebhook(bytes, Sign(body, Now, "whsec_other"), out read).ShouldBeFalse();
            read.ShouldBeNull();
            gateway.TryReadWebhook(Encoding.UTF8.GetBytes(body.Replace("pi_1", "pi_2", StringComparison.Ordinal)), Sign(body, Now), out _).ShouldBeFalse("altered");
            gateway.TryReadWebhook(bytes, Sign(body, Now.AddMinutes(-6)), out _).ShouldBeFalse("too old to trust: a replay");
            gateway.TryReadWebhook(bytes, Sign(body, Now.AddMinutes(-4)), out _).ShouldBeTrue();
            gateway.TryReadWebhook(bytes, "", out _).ShouldBeFalse();
            gateway.TryReadWebhook(bytes, "t=abc,v1=00", out _).ShouldBeFalse();
            gateway.TryReadWebhook(bytes, $"t={Now.ToUnixTimeSeconds()},v1=not-hex", out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("payment_intent.processing", """{"id":"pi_1"}""", "pi_1", PaymentIntentState.Processing)]
    [InlineData("payment_intent.payment_failed", """{"id":"pi_1"}""", "pi_1", PaymentIntentState.RequiresAction)]
    [InlineData("payment_intent.canceled", """{"id":"pi_1"}""", "pi_1", PaymentIntentState.Cancelled)]
    [InlineData("charge.refunded", """{"id":"ch_1","payment_intent":"pi_1","refunded":true}""", "pi_1", PaymentIntentState.Refunded)]
    public void A_signed_event_names_the_intent_and_what_happened_to_it(string type, string data, string reference, PaymentIntentState state)
    {
        (StripePaymentGateway gateway, _, HttpClient client) = Gateway();
        using (client)
        {
            string body = Event(type, data);
            gateway.TryReadWebhook(Encoding.UTF8.GetBytes(body), Sign(body, Now), out PaymentWebhookEvent? read).ShouldBeTrue();
            (read!.ProviderReference, read.State).ShouldBe((reference, state));
        }
    }

    [Theory]
    [InlineData("charge.refunded", """{"id":"ch_1","payment_intent":"pi_1","refunded":false}""")]
    [InlineData("customer.created", """{"id":"cus_1"}""")]
    [InlineData("payment_intent.created", """{"id":"pi_1"}""")]
    public void A_signed_event_about_nothing_a_payment_changes_is_genuine_but_empty(string type, string data)
    {
        (StripePaymentGateway gateway, _, HttpClient client) = Gateway();
        using (client)
        {
            string body = Event(type, data);
            gateway.TryReadWebhook(Encoding.UTF8.GetBytes(body), Sign(body, Now), out PaymentWebhookEvent? read).ShouldBeTrue();
            read.ShouldBeNull();
        }
    }

    [Fact]
    public void A_payment_only_moves_forward_whatever_order_the_events_come_in()
    {
        PaymentEventJob.Moves("requires_action", "processing").ShouldBeTrue();
        PaymentEventJob.Moves("processing", "succeeded").ShouldBeTrue();
        PaymentEventJob.Moves("processing", "requires_action").ShouldBeTrue("an ACH debit that bounced asks for another way to pay");
        PaymentEventJob.Moves("failed", "succeeded").ShouldBeTrue("money taken is recorded");
        PaymentEventJob.Moves("succeeded", "refunded").ShouldBeTrue();

        PaymentEventJob.Moves("succeeded", "processing").ShouldBeFalse("a late event never undoes a payment");
        PaymentEventJob.Moves("succeeded", "requires_action").ShouldBeFalse();
        PaymentEventJob.Moves("succeeded", "succeeded").ShouldBeFalse();
        PaymentEventJob.Moves("refunded", "succeeded").ShouldBeFalse();
        PaymentEventJob.Moves("waived", "succeeded").ShouldBeFalse("a waived fee stays waived; the job logs that it was paid");
        PaymentEventJob.Moves("requires_action", "refunded").ShouldBeFalse();
    }

    [Fact]
    public void Stripe_is_the_gateway_only_with_both_its_secrets()
    {
        static IPaymentGateway With(params (string Key, string Value)[] settings)
        {
            ServiceCollection services = new();
            services.AddPaymentGateway(new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());
            return services.BuildServiceProvider().GetRequiredService<IPaymentGateway>();
        }

        With().ShouldBeOfType<ManualPaymentGateway>();
        With(("Payments:Stripe:SecretKey", "sk_test")).ShouldBeOfType<ManualPaymentGateway>("without a webhook secret nothing could confirm a payment");
        With(("Payments:Stripe:SecretKey", "sk_test"), ("Payments:Stripe:WebhookSecret", Secret)).ShouldBeOfType<StripePaymentGateway>();
    }
}
