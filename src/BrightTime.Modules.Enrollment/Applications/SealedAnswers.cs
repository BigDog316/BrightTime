using System.Text.Json;
using System.Text.Json.Nodes;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Tenancy;

namespace BrightTime.Modules.Enrollment.Applications;

/// <summary>
/// An application's answer snapshot, with sensitive answers sealed under the tenant's data key as
/// <c>{"sealed": …}</c> (D46). Only this class seals or unseals them.
/// </summary>
public sealed class SealedAnswers(IFieldCipher cipher, ITenantContext tenant)
{
    private const string SealedProperty = "sealed";

    /// <summary>The snapshot with sensitive answers unsealed.</summary>
    public async Task<Dictionary<string, JsonElement>> ReadAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> answers = new(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(snapshot);
        foreach (JsonProperty p in document.RootElement.EnumerateObject())
        {
            answers[p.Name] = p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty(SealedProperty, out JsonElement sealedValue)
                ? JsonDocument.Parse(await cipher.DecryptAsync(tenant.TenantId, Convert.FromBase64String(sealedValue.GetString()!), cancellationToken).ConfigureAwait(false) ?? "null").RootElement.Clone()
                : p.Value.Clone();
        }

        return answers;
    }

    public async Task<JsonNode> SealAsync(JsonElement value, CancellationToken cancellationToken = default) =>
        new JsonObject { [SealedProperty] = Convert.ToBase64String((await cipher.EncryptAsync(tenant.TenantId, value.GetRawText(), cancellationToken).ConfigureAwait(false))!) };
}
