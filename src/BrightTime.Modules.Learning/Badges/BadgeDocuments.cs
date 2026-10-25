using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrightTime.Modules.Learning.Badges;

/// <summary>What a credential says, before it is shaped as Open Badges 3.0 or 2.0.</summary>
public sealed record BadgeFacts(
    Uri Site,
    Guid OrgId,
    string IssuerDid,
    string IssuerName,
    Guid BadgeClassId,
    string BadgeName,
    string BadgeDescription,
    string Criteria,
    string VerifySlug,
    DateTimeOffset IssuedOn,
    DateTimeOffset? ExpiresOn,
    string RecipientIdentity,
    string Salt,
    string? Narrative);

/// <summary>
/// The shapes of a badge (D66): the Open Badges 3.0 credential and its RS256 JWT proof, the OB 2.0
/// hosted assertion, badge class and issuer, the DID document, and where each lives. Pure: no
/// database, no vault; the caller signs with what <see cref="SigningInput"/> returns.
/// </summary>
/// <remarks>
/// <para>
/// The recipient is never named. Both versions carry only a salted SHA-256 of an identifier for the
/// young person's account (<c>urn:uuid:…</c>), which proves nothing to anyone who doesn't already
/// hold the credential.
/// </para>
/// <para>
/// The issuer is the organisation, as <c>did:web:{host}:api:v1:public:issuers:{org}</c>, which
/// resolves to <c>/api/v1/public/issuers/{org}/did.json</c> on the tenant's own host: no ledger, and
/// no proxy rule beyond the API's own path.
/// </para>
/// </remarks>
public static class BadgeDocuments
{
    public const string CredentialsContext = "https://www.w3.org/ns/credentials/v2";
    public const string OpenBadgesContext = "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json";
    public const string OpenBadgesSchema = "https://purl.imsglobal.org/spec/ob/v3p0/schema/json/ob_v3p0_achievementcredential_schema.json";
    public const string OpenBadges2Context = "https://w3id.org/openbadges/v2";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    // ---- where things live -------------------------------------------------------------------

    /// <summary>The organisation's DID. A port is percent-encoded, as did:web requires.</summary>
    public static string Did(Uri site, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(site);
        string host = site.IsDefaultPort ? site.Host : $"{site.Host}%3A{site.Port.ToString(CultureInfo.InvariantCulture)}";
        return $"did:web:{host}:api:v1:public:issuers:{orgId:N}";
    }

    public static Uri DidDocument(Uri site, Guid orgId) => new(site, $"api/v1/public/issuers/{orgId:N}/did.json");

    public static Uri Issuer2(Uri site, Guid orgId) => new(site, $"api/v1/public/issuers/{orgId:N}/ob2");

    public static Uri Revocations(Uri site, Guid orgId) => new(site, $"api/v1/public/issuers/{orgId:N}/revocations");

    public static Uri Achievement(Uri site, Guid badgeClassId) => new(site, $"api/v1/public/badges/classes/{badgeClassId:N}");

    public static Uri BadgeClass2(Uri site, Guid badgeClassId) => new(site, $"api/v1/public/badges/classes/{badgeClassId:N}/ob2");

    public static Uri Image(Uri site, Guid badgeClassId) => new(site, $"api/v1/public/badges/classes/{badgeClassId:N}/image.svg");

    /// <summary>The credential's id, and the page that says whether it still stands.</summary>
    public static Uri Credential(Uri site, string verifySlug) => new(site, $"api/v1/public/badges/{verifySlug}");

    public static Uri Assertion2(Uri site, string verifySlug) => new(site, $"api/v1/public/badges/{verifySlug}/ob2");

    // ---- the recipient -----------------------------------------------------------------------

    /// <summary>The identifier both versions hash: the account, as a URN, never an email.</summary>
    public static string RecipientIdentity(Guid userId) => $"urn:uuid:{userId:D}";

    /// <summary>SHA-256 of the identity followed by the salt, as both versions define it.</summary>
    public static byte[] IdentityHash(string identity, string salt) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(identity + salt));

    public static string IdentityHashText(string identity, string salt) =>
        "sha256$" + Convert.ToHexStringLower(IdentityHash(identity, salt));

    // ---- Open Badges 3.0 -----------------------------------------------------------------------

    public static JsonObject Credential(BadgeFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        JsonObject issuer = new() { ["id"] = f.IssuerDid, ["type"] = new JsonArray("Profile"), ["name"] = f.IssuerName, ["url"] = f.Site.ToString() };
        JsonObject credential = new()
        {
            ["@context"] = new JsonArray(CredentialsContext, OpenBadgesContext),
            ["id"] = Credential(f.Site, f.VerifySlug).ToString(),
            ["type"] = new JsonArray("VerifiableCredential", "OpenBadgeCredential"),
            ["issuer"] = issuer,
            ["validFrom"] = Instant(f.IssuedOn),
            ["name"] = f.BadgeName,
            ["credentialSubject"] = new JsonObject
            {
                ["type"] = new JsonArray("AchievementSubject"),
                ["identifier"] = new JsonArray(new JsonObject
                {
                    ["type"] = "IdentityObject",
                    ["identityHash"] = IdentityHashText(f.RecipientIdentity, f.Salt),
                    ["identityType"] = "identifier",
                    ["hashed"] = true,
                    ["salt"] = f.Salt,
                }),
                ["achievement"] = Achievement(f),
            },
            ["credentialSchema"] = new JsonArray(new JsonObject { ["id"] = OpenBadgesSchema, ["type"] = "1EdTechJsonSchemaValidator2019" }),
            ["credentialStatus"] = new JsonObject { ["id"] = Revocations(f.Site, f.OrgId).ToString(), ["type"] = "1EdTechRevocationList" },
        };
        if (f.ExpiresOn is { } expires)
        {
            credential["validUntil"] = Instant(expires);
        }

        if (f.Narrative is { Length: > 0 } narrative)
        {
            credential["credentialSubject"]!["narrative"] = narrative;
        }

        return credential;
    }

    public static JsonObject Achievement(BadgeFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new JsonObject
        {
            ["id"] = Achievement(f.Site, f.BadgeClassId).ToString(),
            ["type"] = new JsonArray("Achievement"),
            ["name"] = f.BadgeName,
            ["description"] = f.BadgeDescription,
            ["criteria"] = new JsonObject { ["narrative"] = f.Criteria },
            ["image"] = new JsonObject { ["id"] = Image(f.Site, f.BadgeClassId).ToString(), ["type"] = "Image" },
            ["creator"] = new JsonObject { ["id"] = f.IssuerDid, ["type"] = new JsonArray("Profile"), ["name"] = f.IssuerName },
        };
    }

    /// <summary>
    /// The JWT's header and payload, base64url-encoded and joined: what is signed. The payload is the
    /// credential with the registered claims beside it, as OB 3.0 § 8.2.4 describes. The header names
    /// the key by its DID URL and carries the public key too, for a verifier that doesn't resolve DIDs.
    /// </summary>
    public static string SigningInput(JsonObject credential, string keyId, string publicJwk, DateTimeOffset issuedOn, DateTimeOffset? expiresOn)
    {
        ArgumentNullException.ThrowIfNull(credential);
        JsonObject header = new() { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = keyId, ["jwk"] = JsonNode.Parse(publicJwk) };
        JsonObject payload = (JsonObject)credential.DeepClone();
        payload["iss"] = credential["issuer"]!["id"]!.GetValue<string>();
        payload["jti"] = credential["id"]!.GetValue<string>();
        payload["nbf"] = issuedOn.ToUnixTimeSeconds();
        if (expiresOn is { } expires)
        {
            payload["exp"] = expires.ToUnixTimeSeconds();
        }

        return WebSafeBase64(Encoding.UTF8.GetBytes(header.ToJsonString(Compact))) + "." + WebSafeBase64(Encoding.UTF8.GetBytes(payload.ToJsonString(Compact)));
    }

    public static string Jwt(string signingInput, byte[] signature) => signingInput + "." + WebSafeBase64(signature);

    /// <summary>RFC 7638's thumbprint of a public JWK: SHA-256 of its required members, in order, with no whitespace.</summary>
    public static string Thumbprint(string publicJwk)
    {
        using JsonDocument jwk = JsonDocument.Parse(publicJwk);
        string canonical = $$"""{"e":"{{jwk.RootElement.GetProperty("e").GetString()}}","kty":"RSA","n":"{{jwk.RootElement.GetProperty("n").GetString()}}"}""";
        return WebSafeBase64(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The issuer's DID document: every key it has signed with and not withdrawn as compromised.</summary>
    public static JsonObject DidDocumentFor(string did, IEnumerable<(string KeyId, string PublicJwk)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        JsonArray methods = [];
        JsonArray assertion = [];
        foreach ((string keyId, string jwk) in keys)
        {
            string id = $"{did}#{keyId}";
            methods.Add(new JsonObject { ["id"] = id, ["type"] = "JsonWebKey2020", ["controller"] = did, ["publicKeyJwk"] = JsonNode.Parse(jwk) });
            assertion.Add(id);
        }

        return new JsonObject
        {
            ["@context"] = new JsonArray("https://www.w3.org/ns/did/v1", "https://w3id.org/security/suites/jws-2020/v1"),
            ["id"] = did,
            ["verificationMethod"] = methods,
            ["assertionMethod"] = assertion,
        };
    }

    // ---- Open Badges 2.0, hosted ----------------------------------------------------------------

    public static JsonObject Assertion2(BadgeFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        JsonObject assertion = new()
        {
            ["@context"] = OpenBadges2Context,
            ["type"] = "Assertion",
            ["id"] = Assertion2(f.Site, f.VerifySlug).ToString(),
            ["recipient"] = new JsonObject
            {
                ["type"] = "url",
                ["hashed"] = true,
                ["salt"] = f.Salt,
                ["identity"] = IdentityHashText(f.RecipientIdentity, f.Salt),
            },
            ["badge"] = BadgeClass2(f.Site, f.BadgeClassId).ToString(),
            ["issuedOn"] = Instant(f.IssuedOn),
            ["verification"] = new JsonObject { ["type"] = "hosted" },
        };
        if (f.ExpiresOn is { } expires)
        {
            assertion["expires"] = Instant(expires);
        }

        if (f.Narrative is { Length: > 0 } narrative)
        {
            assertion["narrative"] = narrative;
        }

        return assertion;
    }

    public static JsonObject BadgeClass2(Uri site, Guid orgId, Guid badgeClassId, string name, string description, string criteria) => new()
    {
        ["@context"] = OpenBadges2Context,
        ["type"] = "BadgeClass",
        ["id"] = BadgeClass2(site, badgeClassId).ToString(),
        ["name"] = name,
        ["description"] = description,
        ["image"] = Image(site, badgeClassId).ToString(),
        ["criteria"] = new JsonObject { ["narrative"] = criteria },
        ["issuer"] = Issuer2(site, orgId).ToString(),
    };

    public static JsonObject Issuer2Document(Uri site, Guid orgId, string name)
    {
        ArgumentNullException.ThrowIfNull(site);
        return new JsonObject
        {
            ["@context"] = OpenBadges2Context,
            ["type"] = "Issuer",
            ["id"] = Issuer2(site, orgId).ToString(),
            ["name"] = name,
            ["url"] = site.ToString(),
        };
    }

    /// <summary>What a hosted assertion says once revoked (OB 2.0 § Hosted Verification), served with 410 Gone.</summary>
    public static JsonObject Revoked2(Uri site, string verifySlug, string reason) => new()
    {
        ["@context"] = OpenBadges2Context,
        ["type"] = "Assertion",
        ["id"] = Assertion2(site, verifySlug).ToString(),
        ["revoked"] = true,
        ["revocationReason"] = reason,
    };

    // ---- the image -----------------------------------------------------------------------------

    /// <summary>
    /// A plain badge image for a class with none of its own: its initials on a ring. OB 2.0 requires
    /// an image, and media storage isn't decided yet. Escaped, so a name can't inject markup.
    /// </summary>
    public static string ImageSvg(string name, string issuer)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(issuer);
        string initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        int hue = (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0) % 360);
        string fill = $"hsl({hue.ToString(CultureInfo.InvariantCulture)} 55% 38%)";
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" role="img" aria-label="{Xml(name)}, {Xml(issuer)}">
              <title>{Xml(name)}</title>
              <circle cx="100" cy="100" r="92" fill="{fill}"/>
              <circle cx="100" cy="100" r="78" fill="none" stroke="#fff" stroke-width="4"/>
              <text x="100" y="118" text-anchor="middle" font-family="system-ui, sans-serif" font-size="56" font-weight="700" fill="#fff">{Xml(initials)}</text>
            </svg>
            """;
    }

    private static string Xml(string text) => System.Security.SecurityElement.Escape(text) ?? string.Empty;

    internal static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string WebSafeBase64(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
