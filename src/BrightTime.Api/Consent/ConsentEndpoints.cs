using System.Globalization;
using System.Security.Claims;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Mail;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api.Consent;

public sealed record CreateChildRequest(string? GivenName, string? BirthMonth, string? Relationship);

public sealed record GrantConsentRequest(string? Scope, string? DisclosureHash);

/// <summary>The young person's own address, for their claim email (D35).</summary>
public sealed record ClaimRequest(string? Email);

/// <summary>The token from the guardian's consent email (the fragment of its link).</summary>
public sealed record VerifyLinkRequest(string? Token);

public sealed record LinkResponse(Guid Id, string Relationship, string Status, string? VerificationMethod, DateTimeOffset? VerifiedAt);

public sealed record ConsentResponse(string Scope, string Method, DateTimeOffset GrantedAt);

public sealed record CapabilitiesResponse(
    bool IsUnder13,
    bool HasAccountConsent,
    bool CanSelfEnroll,
    bool CanPostInCommunity,
    bool CanUploadMedia,
    bool CanPublishShowcase,
    bool CanReceiveSms,
    bool CanBeMessagedByOrganisation)
{
    public static CapabilitiesResponse From(YouthCapabilities c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.IsUnder13, c.HasAccountConsent, c.CanSelfEnroll, c.CanPostInCommunity, c.CanUploadMedia,
            c.CanPublishShowcase, c.CanReceiveSms, c.CanBeMessagedByOrganisation);
    }
}

public sealed record ChildResponse(
    Guid Id,
    string GivenName,
    string BirthMonth,
    string AccountKind,
    string Status,
    LinkResponse Link,
    IReadOnlyList<ConsentResponse> Consents,
    CapabilitiesResponse Capabilities);

public sealed record DisclosureResponse(string Scope, string Version, string Text, string Hash, IReadOnlyList<string> Methods, bool AvailableUnder13);

/// <summary>
/// Guardians, child profiles and parental consent (docs/plan/05, "Identity &amp; consent"), over
/// <see cref="Guardianship"/>, which decides who may do what.
/// </summary>
/// <remarks>
/// <para>
/// A guardian is any adult account with a confirmed address. A child profile does nothing until
/// the guardian's link is verified: by email plus, or by a signed form an organiser reviews in
/// person (<c>POST /organiser/guardian-links/{id}/attest</c>).
/// </para>
/// <para>
/// Email plus: making a profile sends the guardian a consent email with the account disclosure
/// and a single-use link. The app answers it with <c>POST /guardian-links/{id}/verify</c>,
/// signed in as that guardian, and a confirmatory email follows. A lost or expired email is
/// replaced with <c>POST /guardian-links/{id}/consent-email</c>.
/// </para>
/// </remarks>
public static class ConsentEndpoints
{
    public const string AttestPolicy = "consent.attest";

    public static RouteGroupBuilder MapConsentEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/consent/disclosures", Disclosures).RequireAuthorization();

        RouteGroupBuilder children = api.MapGroup("/children").RequireAuthorization();
        children.MapPost("/", CreateAsync);
        children.MapGet("/", ListAsync);
        children.MapGet("/{id:guid}", GetAsync);
        children.MapPost("/{id:guid}/consents", GrantAsync);
        children.MapDelete("/{id:guid}/consents/{scope}", RevokeAsync);
        children.MapPost("/{id:guid}/claim", ClaimAsync).RequireRateLimiting(ApiComposition.SignInLimit);

        api.MapPost("/guardian-links/{id:guid}/verify", VerifyLinkAsync).RequireAuthorization();
        api.MapPost("/guardian-links/{id:guid}/consent-email", ResendConsentEmailAsync).RequireAuthorization();
        api.MapPost("/guardian-links/{id:guid}/revoke", RevokeLinkAsync).RequireAuthorization();
        api.MapPost("/organiser/guardian-links/{id:guid}/attest", AttestAsync).RequireAuthorization(AttestPolicy);

        api.MapGet("/me/capabilities", CapabilitiesAsync).RequireAuthorization();
        return api;
    }

    private static IResult Disclosures() => Results.Ok(ConsentDisclosures.All
        .OrderBy(d => d.Scope)
        .Select(d => new DisclosureResponse(d.Scope.ToDbValue(), d.Version, d.Text, d.HashHex, [.. d.Methods.Order(StringComparer.Ordinal)], d.AvailableUnder13))
        .ToList());

    private static async Task<IResult> CreateAsync(
        CreateChildRequest body,
        ClaimsPrincipal user,
        Guardianship guardianship,
        AccountMailDispatcher mail,
        ITenantContext tenant,
        SiteContext site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!DateOnly.TryParseExact(body.BirthMonth, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly month))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["birthMonth"] = ["A month as yyyy-MM."] });
        }

        Result<NewChild> made = await guardianship.CreateChildAsync(Actor(user), body.GivenName ?? string.Empty, month, body.Relationship ?? string.Empty, ct);
        if (!made.IsSuccess)
        {
            return ApiProblems.From(made.Error);
        }

        mail.RequestConsent(tenant.TenantId, made.Value.ConsentEmail, SiteUrl(site), tenant.Locale);
        return Results.Created($"/api/v1/children/{made.Value.Child.Id.Value}", Child(made.Value.Child));
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, Guardianship guardianship, CancellationToken ct) =>
        Results.Ok((await guardianship.ChildrenAsync(Actor(user), ct)).Select(Child).ToList());

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, Guardianship guardianship, CancellationToken ct) =>
        await guardianship.ChildAsync(Actor(user), new UserId(id), ct) is { } child
            ? Results.Ok(Child(child))
            : ApiProblems.NotFound("You have no child profile with that id.");

    private static async Task<IResult> GrantAsync(Guid id, GrantConsentRequest body, ClaimsPrincipal user, Guardianship guardianship, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!ConsentScopeExtensions.TryFromDbValue(body.Scope, out ConsentScope scope))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["scope"] = ["Not a consent scope; see /consent/disclosures."] });
        }

        Result<ConsentView> granted = await guardianship.GrantAsync(Actor(user), new UserId(id), scope, body.DisclosureHash, ct);
        return granted.IsSuccess
            ? Results.Ok(new ConsentResponse(granted.Value.Scope.ToDbValue(), granted.Value.Method, granted.Value.GrantedAt))
            : ApiProblems.From(granted.Error);
    }

    private static async Task<IResult> RevokeAsync(Guid id, string scope, ClaimsPrincipal user, Guardianship guardianship, CancellationToken ct)
    {
        if (!ConsentScopeExtensions.TryFromDbValue(scope, out ConsentScope parsed))
        {
            return ApiProblems.NotFound("Not a consent scope.");
        }

        Result revoked = await guardianship.RevokeAsync(Actor(user), new UserId(id), parsed, ct);
        return revoked.IsSuccess ? Results.NoContent() : ApiProblems.From(revoked.Error);
    }

    /// <summary>
    /// The guardian asks that their child, now 13, have a sign-in of their own (D35). 202 whatever the
    /// address: the claim email, or a word to an address that already has an account, goes after the
    /// response. Rate limited, since each request sends an email.
    /// </summary>
    private static async Task<IResult> ClaimAsync(
        Guid id,
        ClaimRequest body,
        ClaimsPrincipal user,
        Guardianship guardianship,
        AccountMailDispatcher mail,
        ITenantContext tenant,
        SiteContext site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<ClaimAsk> asked = await guardianship.RequestClaimAsync(Actor(user), new UserId(id), body.Email, ct);
        if (!asked.IsSuccess)
        {
            return ApiProblems.From(asked.Error);
        }

        mail.RequestClaim(tenant.TenantId, asked.Value, SiteUrl(site), tenant.Locale);
        return Results.Accepted();
    }

    /// <summary>The guardian's answer to their consent email. Answers with the child, now active.</summary>
    private static async Task<IResult> VerifyLinkAsync(
        Guid id,
        VerifyLinkRequest body,
        ClaimsPrincipal user,
        Guardianship guardianship,
        AccountMailDispatcher mail,
        ITenantContext tenant,
        SiteContext site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        UserId guardian = Actor(user);
        Result<ConsentConfirmation> verified = await guardianship.VerifyByEmailAsync(guardian, id, body.Token, ct);
        if (!verified.IsSuccess)
        {
            return ApiProblems.From(verified.Error);
        }

        // The "plus" of email plus: a confirmatory email to the same address.
        mail.RequestConsentConfirmation(tenant.TenantId, verified.Value, SiteUrl(site), tenant.Locale);
        return await guardianship.ChildAsync(guardian, verified.Value.ChildId, ct) is { } child
            ? Results.Ok(Child(child))
            : ApiProblems.NotFound("You have no child profile with that id.");
    }

    private static async Task<IResult> ResendConsentEmailAsync(
        Guid id,
        ClaimsPrincipal user,
        Guardianship guardianship,
        AccountMailDispatcher mail,
        ITenantContext tenant,
        SiteContext site,
        CancellationToken ct)
    {
        Result<ConsentEmail> issued = await guardianship.ResendConsentEmailAsync(Actor(user), id, ct);
        if (!issued.IsSuccess)
        {
            return ApiProblems.From(issued.Error);
        }

        mail.RequestConsent(tenant.TenantId, issued.Value, SiteUrl(site), tenant.Locale);
        return Results.Accepted();
    }

    private static async Task<IResult> RevokeLinkAsync(Guid id, ClaimsPrincipal user, Guardianship guardianship, CancellationToken ct)
    {
        Result revoked = await guardianship.RevokeLinkAsync(Actor(user), id, ct);
        return revoked.IsSuccess ? Results.NoContent() : ApiProblems.From(revoked.Error);
    }

    private static async Task<IResult> AttestAsync(Guid id, ClaimsPrincipal user, Guardianship guardianship, CancellationToken ct)
    {
        Result<GuardianLinkView> attested = await guardianship.AttestSignedFormAsync(id, Actor(user), ct);
        return attested.IsSuccess ? Results.Ok(Link(attested.Value)) : ApiProblems.From(attested.Error);
    }

    /// <summary>What the signed-in person may do, so the app gates on the server's answer rather than its own guess.</summary>
    private static async Task<IResult> CapabilitiesAsync(ClaimsPrincipal user, IIdentityQueries identity, CancellationToken ct) =>
        Results.Ok(CapabilitiesResponse.From(await identity.GetCapabilitiesAsync(Actor(user), ct)));

    /// <summary>The tenant's own address, which every link in an email is built from; never the request's Host.</summary>
    private static Uri SiteUrl(SiteContext site) =>
        site.BaseUrl ?? throw new InvalidOperationException("A resolved tenant has an address.");

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));

    private static ChildResponse Child(ChildView c) => new(
        c.Id.Value,
        c.GivenName,
        c.BirthMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        c.AccountKind,
        c.Status,
        Link(c.Link),
        [.. c.Consents.Select(x => new ConsentResponse(x.Scope.ToDbValue(), x.Method, x.GrantedAt))],
        CapabilitiesResponse.From(c.Capabilities));

    private static LinkResponse Link(GuardianLinkView l) => new(l.Id, l.Relationship, l.Status, l.VerificationMethod, l.VerifiedAt);
}
