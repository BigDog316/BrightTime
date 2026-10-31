using System.Security.Claims;
using BrightTime.Abstractions.Payments;
using BrightTime.Api.Forms;
using BrightTime.Hosting.Tenancy;
using BrightTime.Modules.Enrollment.Payments;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Payments;

public sealed record FeesRequest(IReadOnlyList<FeeInput>? Fees);

public sealed record PaymentRequest(Guid? ApplicationId, Guid? EnrollmentId, Guid FeeScheduleId, Guid? TierId);

public sealed record ReasonRequest(string? Reason);

/// <summary>
/// Fees and payments (M3b, D55): an organisation's admins set a listing's fees and sliding-scale
/// tiers, waive a fee, or record money received outside the platform; a family sees what it is
/// asked to pay and pays through the configured provider. No card data passes through here (T20).
/// </summary>
public static class PaymentEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>The site's page a provider's hosted payment page returns to (D74). It says go back to the app; it settles nothing.</summary>
    public const string ReturnPath = "/payment/done";

    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder org = api.MapGroup("/org/{orgId:guid}").RequireAuthorization();
        org.MapGet("/listings/{listingId:guid}/fees", FeesAsync);
        org.MapPut("/listings/{listingId:guid}/fees", SetFeesAsync);
        org.MapGet("/listings/{listingId:guid}/payments", ListAsync);
        org.MapPost("/payments/{id:guid}/waive", WaiveAsync);
        org.MapPost("/payments/{id:guid}/received", ReceivedAsync);

        api.MapGet("/applications/{id:guid}/fees", QuoteAsync).RequireAuthorization();
        api.MapGet("/applications/{id:guid}/payments", ForApplicationAsync).RequireAuthorization();
        api.MapPost("/payments/intents", PayAsync).RequireAuthorization();
        api.MapGet("/payments/intents/{id:guid}", GetAsync).RequireAuthorization();

        // The provider's callbacks: anonymous, trusted only by signature (D56). Not rate-limited by
        // address, since every callback comes from the provider's few; a forged one costs an HMAC.
        api.MapPost("/webhooks/payments/{provider}", WebhookAsync).AllowAnonymous();
        return api;
    }

    private static async Task<IResult> FeesAsync(Guid orgId, Guid listingId, ClaimsPrincipal user, IAuthorizationService authorization, FeeSchedules fees, CancellationToken ct) =>
        await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Answer(await fees.GetAsync(new OrganisationId(orgId), listingId, ct));

    private static async Task<IResult> SetFeesAsync(Guid orgId, Guid listingId, FeesRequest body, ClaimsPrincipal user, IAuthorizationService authorization, FeeSchedules fees, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await FormEndpoints.RefusedAsync(user, orgId, authorization) ?? Answer(await fees.SetAsync(new OrganisationId(orgId), listingId, body.Fees ?? [], ct));
    }

    private static async Task<IResult> ListAsync(Guid orgId, Guid listingId, ClaimsPrincipal user, IAuthorizationService authorization, PaymentIntents payments, CancellationToken ct)
    {
        if (await WaiverRefusedAsync(user, orgId, authorization) is { } refused)
        {
            return refused;
        }

        Result<IReadOnlyList<PaymentView>> found = await payments.ForListingAsync(new OrganisationId(orgId), listingId, ct);
        return found.IsSuccess ? Results.Ok(found.Value) : ApiProblems.From(found.Error);
    }

    private static async Task<IResult> WaiveAsync(Guid orgId, Guid id, ReasonRequest body, ClaimsPrincipal user, IAuthorizationService authorization, PaymentIntents payments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await WaiverRefusedAsync(user, orgId, authorization) ?? Answer(await payments.WaiveAsync(new OrganisationId(orgId), id, body.Reason ?? string.Empty, ct));
    }

    private static async Task<IResult> ReceivedAsync(Guid orgId, Guid id, ReasonRequest body, ClaimsPrincipal user, IAuthorizationService authorization, PaymentIntents payments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return await WaiverRefusedAsync(user, orgId, authorization) ?? Answer(await payments.RecordReceivedAsync(new OrganisationId(orgId), id, body.Reason, ct));
    }

    private static async Task<IResult> QuoteAsync(Guid id, ClaimsPrincipal user, FeeSchedules fees, CancellationToken ct)
    {
        Result<FeeQuote> quote = await fees.QuoteAsync(Actor(user), id, ct);
        return quote.IsSuccess ? Results.Ok(quote.Value) : ApiProblems.From(quote.Error);
    }

    private static async Task<IResult> ForApplicationAsync(Guid id, ClaimsPrincipal user, PaymentIntents payments, CancellationToken ct)
    {
        Result<IReadOnlyList<PaymentView>> found = await payments.ForApplicationAsync(Actor(user), id, ct);
        return found.IsSuccess ? Results.Ok(found.Value) : ApiProblems.From(found.Error);
    }

    /// <summary>Pays a fee. Needs an Idempotency-Key; while a payment for the same place is open, that one comes back.</summary>
    private static async Task<IResult> PayAsync(PaymentRequest body, HttpRequest request, ClaimsPrincipal user, SiteContext site, PaymentIntents payments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(site);
        Uri address = site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");
        bool adult = (user.FindFirstValue(BrightTimeClaims.AccountKind) ?? "adult") == "adult";
        Result<PaymentView> made = await payments.CreateAsync(Actor(user), adult, body.ApplicationId, body.EnrollmentId, body.FeeScheduleId, body.TierId,
            request.Headers[IdempotencyHeader].ToString(), new Uri(address, ReturnPath), ct);
        return made.IsSuccess ? Results.Created($"/api/v1/payments/intents/{made.Value.Id}", made.Value) : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, PaymentIntents payments, CancellationToken ct) =>
        Answer(await payments.GetAsync(Actor(user), id, ct));

    private static async Task<IResult> WebhookAsync(string provider, HttpRequest request, IPaymentGateway gateway, PaymentWebhooks webhooks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[]? payload = await ReadCappedAsync(request.Body, PaymentWebhooks.MaxPayloadBytes, ct);
        if (payload is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        string signature = gateway.WebhookSignatureHeader.Length == 0 ? string.Empty : request.Headers[gateway.WebhookSignatureHeader].ToString();
        Result<bool> accepted = await webhooks.AcceptAsync(provider, payload, signature, ct);
        return accepted.IsSuccess ? Results.Ok() : ApiProblems.From(accepted.Error);
    }

    /// <summary>The body, or null when it is longer than <paramref name="limit"/>.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int limit, CancellationToken ct)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }

    private static async Task<IResult?> WaiverRefusedAsync(ClaimsPrincipal user, Guid orgId, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = new OrganisationId(orgId) },
            new OrganisationScopeRequirement(Permissions.Payments.Waive, OrganisationRoles.Admin))).Succeeded
            ? null
            : ApiProblems.From(Error.Forbidden("payments.forbidden", "Only an owner or admin of this organisation manages its payments."));

    private static IResult Answer<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : ApiProblems.From(result.Error);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
