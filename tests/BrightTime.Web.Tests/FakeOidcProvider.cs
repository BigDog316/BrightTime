using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BrightTime.Web.Tests;

/// <summary>
/// An OpenID Connect provider in miniature: it answers the authorize redirect (as the person's
/// browser would see it), and the code exchange (as the server's back channel would see it),
/// with a real RS256-signed ID token. ASP.NET Core's handler then does all of its checks for
/// real: state, nonce, PKCE round trip, signature, issuer, audience, lifetime.
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.example.test";
    public const string ClientId = "brighttime-test";
    public const string Key = "testidp";
    public const string DisplayName = "Test Identity";

    private readonly RSA _signing = RSA.Create(2048);
    private readonly RSA _impostor = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new();

    private sealed record Grant(string Subject, string Nonce, string? Email, bool WrongKey, string? WrongIssuer);

    public OpenIdConnectConfiguration Configuration { get; }

    public FakeOidcProvider()
    {
        Configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = Issuer + "/authorize",
            TokenEndpoint = Issuer + "/token",
        };
        Configuration.SigningKeys.Add(new RsaSecurityKey(_signing) { KeyId = "real" });
    }

    /// <summary>
    /// The person signs in at the provider: returns where the provider sends the browser back,
    /// carrying a one-time code bound to the nonce the app asked for.
    /// </summary>
    public string Authorize(Uri authorizeRequest, string subject, string? email = null, bool wrongKey = false, string? wrongIssuer = null, string? wrongNonce = null)
    {
        ArgumentNullException.ThrowIfNull(authorizeRequest);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(authorizeRequest.Query);
        query["client_id"].ToString().ShouldBe(ClientId);
        query["response_type"].ToString().ShouldBe("code");
        query["code_challenge_method"].ToString().ShouldBe("S256", "PKCE is on");

        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new Grant(subject, wrongNonce ?? query["nonce"].ToString(), email, wrongKey, wrongIssuer);
        return QueryHelpers.AddQueryString(query["redirect_uri"].ToString(), new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = query["state"].ToString(),
        });
    }

    /// <summary>The back channel: only the token endpoint is ever called (discovery is configured statically).</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri?.AbsoluteUri != Configuration.TokenEndpoint || request.Content is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> form =
            QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(cancellationToken));
        if (form["code_verifier"].ToString().Length < 43 || !_codes.TryRemove(form["code"].ToString(), out Grant? grant))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json") };
        }

        Dictionary<string, object> claims = new() { ["sub"] = grant.Subject, ["nonce"] = grant.Nonce };
        if (grant.Email is not null)
        {
            claims["email"] = grant.Email;
            claims["email_verified"] = true;
        }

        string idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = grant.WrongIssuer ?? Issuer,
            Audience = ClientId,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(grant.WrongKey ? _impostor : _signing) { KeyId = "real" }, SecurityAlgorithms.RsaSha256),
        });

        string body = $"{{\"access_token\":\"opaque\",\"token_type\":\"Bearer\",\"expires_in\":300,\"id_token\":\"{idToken}\"}}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _signing.Dispose();
            _impostor.Dispose();
        }

        base.Dispose(disposing);
    }
}
