using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BrightTime.Web.Tests;

/// <summary>
/// A passkey authenticator in software: what a security key or phone does, so the server's
/// WebAuthn verification runs for real in tests. P-256, "none" attestation, user present and
/// verified, a signature counter that only goes up unless a test says otherwise.
/// </summary>
public sealed class SoftwarePasskey(string origin, string rpId) : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public byte[] UserHandle { get; private set; } = [];

    /// <summary>The next signature counter to report.</summary>
    public uint Counter { get; set; }

    /// <summary>Answers <c>navigator.credentials.create</c> for the given options.</summary>
    public JsonObject Create(JsonNode options)
    {
        ArgumentNullException.ThrowIfNull(options);
        UserHandle = Decode64(options["user"]!["id"]!.GetValue<string>());
        byte[] clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>(), origin);

        byte[] authData =
        [
            .. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)),
            0x45, // user present, user verified, attested credential data included
            .. BigEndian(Counter),
            .. new byte[16], // AAGUID: none
            (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length,
            .. CredentialId,
            .. CosePublicKey(),
        ];

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authData);
        attestation.WriteEndMap();

        return Credential(new JsonObject
        {
            ["attestationObject"] = Encode64(attestation.Encode()),
            ["clientDataJSON"] = Encode64(clientData),
            ["transports"] = new JsonArray("internal"),
        });
    }

    /// <summary>Answers <c>navigator.credentials.get</c>. The overrides make a deliberately bad answer.</summary>
    public JsonObject Get(JsonNode options, string? asOrigin = null, bool tamper = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>(), asOrigin ?? origin);
        byte[] authData =
        [
            .. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)),
            0x05, // user present, user verified
            .. BigEndian(Counter),
        ];

        byte[] signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (tamper)
        {
            signature[^1] ^= 0xFF;
        }

        Counter++;
        return Credential(new JsonObject
        {
            ["authenticatorData"] = Encode64(authData),
            ["clientDataJSON"] = Encode64(clientData),
            ["signature"] = Encode64(signature),
            ["userHandle"] = Encode64(UserHandle),
        });
    }

    public void Dispose() => _key.Dispose();

    private JsonObject Credential(JsonObject response) => new()
    {
        ["id"] = Encode64(CredentialId),
        ["rawId"] = Encode64(CredentialId),
        ["type"] = "public-key",
        ["response"] = response,
        ["clientExtensionResults"] = new JsonObject(),
    };

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = origin,
            ["crossOrigin"] = false,
        }.ToJsonString());

    /// <summary>The public key as COSE: EC2, ES256, P-256, x and y.</summary>
    private byte[] CosePublicKey()
    {
        ECParameters p = _key.ExportParameters(false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1);
        cose.WriteInt32(2);
        cose.WriteInt32(3);
        cose.WriteInt32(-7);
        cose.WriteInt32(-1);
        cose.WriteInt32(1);
        cose.WriteInt32(-2);
        cose.WriteByteString(p.Q.X!);
        cose.WriteInt32(-3);
        cose.WriteByteString(p.Q.Y!);
        cose.WriteEndMap();
        return cose.Encode();
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    public static string Encode64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode64(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64 + new string('=', (4 - (base64.Length % 4)) % 4));
    }
}
