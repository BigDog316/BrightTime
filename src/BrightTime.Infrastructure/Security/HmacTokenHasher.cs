using System.Security.Cryptography;
using System.Text;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Security;

/// <summary>
/// A keyed hash of an opaque token (a push token, a link token), exact to the byte. The blind
/// indexer folds case and whitespace, which suits an email address and would merge two
/// case-sensitive tokens. Each purpose is its own domain, so a hash made for one cannot be
/// replayed as another.
/// </summary>
public sealed class HmacTokenHasher(ITenantDataKeyProvider keys)
{
    public async ValueTask<byte[]> HashAsync(TenantId tenant, string purpose, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrEmpty(token);

        TenantDataKey key = await keys.GetCurrentAsync(tenant, cancellationToken).ConfigureAwait(false);
        byte[] bytes = Encoding.UTF8.GetBytes(purpose + "\n" + token);
        try
        {
            return HMACSHA256.HashData(key.Span, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
