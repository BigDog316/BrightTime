using System.Security.Claims;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.Consent;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api.Me;

/// <summary>The terms of use: their exact words, and the hash a person agrees to.</summary>
public sealed record TermsResponse(string Kind, string Version, string Text, string Hash);

/// <summary>Where the signed-in person stands with the current terms.</summary>
public sealed record MyTermsResponse(string Version, string Hash, DateTimeOffset? AcceptedAt);

public sealed record AcceptTermsRequest(string? Hash);

/// <summary>
/// The terms of use (D37): anyone may read them; a signed-in person agrees to the version they
/// were shown, by its hash. A young person's agreement lets them sign up for programs on their own.
/// </summary>
public static class TermsEndpoints
{
    public static RouteGroupBuilder MapTermsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/terms", Terms).AllowAnonymous();
        RouteGroupBuilder me = api.MapGroup("/me").RequireAuthorization();
        me.MapGet("/terms", MineAsync);
        me.MapPost("/terms", AcceptAsync);
        return api;
    }

    private static IResult Terms() =>
        Results.Ok(new TermsResponse(AccountTerms.Current.Kind, AccountTerms.Current.Version, AccountTerms.Current.Text, AccountTerms.Current.HashHex));

    private static async Task<IResult> MineAsync(ClaimsPrincipal user, AccountTerms terms, CancellationToken ct) =>
        Results.Ok(Mine(await terms.StateAsync(Actor(user), ct)));

    private static async Task<IResult> AcceptAsync(AcceptTermsRequest body, ClaimsPrincipal user, AccountTerms terms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<TermsState> accepted = await terms.AcceptAsync(Actor(user), body.Hash, ct);
        return accepted.IsSuccess ? Results.Ok(Mine(accepted.Value)) : ApiProblems.From(accepted.Error);
    }

    private static MyTermsResponse Mine(TermsState state) => new(state.Current.Version, state.Current.HashHex, state.AcceptedAt);

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
