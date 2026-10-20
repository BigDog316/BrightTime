using System.Security.Cryptography;
using System.Xml.Linq;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BrightTime.Hosting.Security;

/// <summary>
/// The data protection key ring in <c>platform.data_protection_key</c> (D39), so every instance of a
/// host reads the same keys: a cookie, an antiforgery token or a passkey ceremony made on one
/// instance opens on another, and survives a restart.
/// </summary>
/// <remarks>Append-only, as the grants make it. Data protection retires a key by writing a new one.</remarks>
public sealed class SqlXmlRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    // Data protection's interface is synchronous; these are two short statements on start and on rotation.
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using NpgsqlConnection connection = dataSource.OpenConnection();
        using NpgsqlCommand command = new("select xml from platform.data_protection_key order by id", connection);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<XElement> elements = [];
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using NpgsqlConnection connection = dataSource.OpenConnection();
        using NpgsqlCommand command = new(
            """
            insert into platform.data_protection_key (friendly_name, xml) values (@name, @xml)
            on conflict (friendly_name) do nothing
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("name", friendlyName));
        command.Parameters.Add(new NpgsqlParameter("xml", element.ToString(SaveOptions.DisableFormatting)));
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Encrypts each key before it is written: AES-256-GCM under a vault key (<c>Security:Keys</c>,
/// D32), so the table holds nothing usable without the vault. The element names its key reference,
/// so a key written under an older reference still opens while that reference is configured.
/// </summary>
public sealed class VaultXmlEncryptor(IKeyVault vault, string keyRef) : IXmlEncryptor
{
    internal const string ElementName = "vaultEncryptedKey";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];
        using (AesGcm aes = new(Key(vault, keyRef), TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(keyRef));
        }

        CryptographicOperations.ZeroMemory(plaintext);
        return new EncryptedXmlInfo(
            new XElement(ElementName,
                new XAttribute("keyRef", keyRef),
                new XAttribute("nonce", Convert.ToBase64String(nonce)),
                new XAttribute("tag", Convert.ToBase64String(tag)),
                Convert.ToBase64String(ciphertext)),
            typeof(VaultXmlDecryptor));
    }

    // The vault's interface is asynchronous and data protection's is not; the configured vault
    // answers at once, and a managed vault's client caches the key after the first read.
    internal static byte[] Key(IKeyVault vault, string keyRef)
    {
        ReadOnlyMemory<byte> key = vault.GetSymmetricKeyAsync(keyRef).AsTask().GetAwaiter().GetResult();
        return key.Span[..32].ToArray();
    }

    // The reference is bound in, so a key cannot be passed off as written under another.
    internal static byte[] AssociatedData(string keyRef) => System.Text.Encoding.UTF8.GetBytes("brighttime.dataprotection:" + keyRef);
}

/// <summary>Opens what <see cref="VaultXmlEncryptor"/> wrote. Made by data protection with the host's services.</summary>
public sealed class VaultXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        string keyRef = (string?)encryptedElement.Attribute("keyRef") ?? throw new CryptographicException("The key has no vault reference.");
        byte[] nonce = Convert.FromBase64String((string?)encryptedElement.Attribute("nonce") ?? string.Empty);
        byte[] tag = Convert.FromBase64String((string?)encryptedElement.Attribute("tag") ?? string.Empty);
        byte[] ciphertext = Convert.FromBase64String(encryptedElement.Value);
        byte[] plaintext = new byte[ciphertext.Length];
        using (AesGcm aes = new(VaultXmlEncryptor.Key(services.GetRequiredService<IKeyVault>(), keyRef), tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, VaultXmlEncryptor.AssociatedData(keyRef));
        }

        try
        {
            return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

public static class DataProtectionKeys
{
    public const string SectionName = "DataProtection";

    /// <summary>The vault key the key ring is encrypted under, unless <c>DataProtection:KeyRef</c> names another.</summary>
    public const string DefaultKeyRef = "data-protection";

    /// <summary>
    /// Data protection for a host: its own application name, and with a database the shared,
    /// encrypted key ring (D39). Without one (the web's sample mode) the framework's default
    /// per-machine keys stay.
    /// </summary>
    public static IServiceCollection AddBrightTimeDataProtection(this IServiceCollection services, IConfiguration configuration, string applicationName, bool development, bool hasDatabase)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddDataProtection().SetApplicationName(applicationName);
        if (!hasDatabase)
        {
            return services;
        }

        services.TryAddSingleton<IKeyVault>(_ => new ConfiguredKeyVault(configuration, development));
        string keyRef = configuration[$"{SectionName}:KeyRef"] is { Length: > 0 } configured ? configured : DefaultKeyRef;
        services.AddOptions<KeyManagementOptions>().Configure<NpgsqlDataSource, IKeyVault>((options, dataSource, vault) =>
        {
            options.XmlRepository = new SqlXmlRepository(dataSource);
            options.XmlEncryptor = new VaultXmlEncryptor(vault, keyRef);
        });
        return services;
    }
}
