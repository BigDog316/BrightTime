using System.Security.Cryptography;
using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Media;

/// <summary>
/// Media bytes in <c>platform.media_blob</c> (D103), on the Platform context, so a write joins the
/// caller's transaction there and the tenant policy keeps each tenant's bytes its own.
/// </summary>
public sealed class DatabaseObjectStore(PlatformDbContext db, ITenantContext tenant) : IObjectStore
{
    public Task<StoredObject> PutAsync(string objectKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        byte[] bytes = content.ToArray();
        byte[] sha = SHA256.HashData(bytes);
        return db.InTransactionAsync(async ct =>
        {
            db.MediaBlobs.Add(new MediaBlob
            {
                TenantId = tenant.TenantId.Value,
                ObjectKey = objectKey,
                ContentType = contentType,
                Bytes = bytes,
                Sha256 = sha,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new StoredObject(objectKey, contentType, bytes.LongLength, sha);
        }, cancellationToken);
    }

    public Task<byte[]?> ReadAsync(string objectKey, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => db.MediaBlobs.AsNoTracking()
            .Where(b => b.TenantId == tenant.TenantId.Value && b.ObjectKey == objectKey)
            .Select(b => b.Bytes)
            .FirstOrDefaultAsync(ct), cancellationToken);

    public Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await db.MediaBlobs.Where(b => b.TenantId == tenant.TenantId.Value && b.ObjectKey == objectKey).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0, cancellationToken);
}
