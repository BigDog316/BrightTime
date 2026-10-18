using System.Security.Cryptography;
using System.Text;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Hosting.Auditing;

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>
    /// Base64 key (32 bytes or more) for hashing client addresses and user agents. Required
    /// with a database outside Development; Development makes one up per run.
    /// </summary>
    public string? ClientHashKey { get; set; }
}

/// <summary>The request as the audit trail records it: no raw address, no raw user agent.</summary>
/// <remarks>
/// An address alone is low-entropy enough to recover from a plain hash, so both are HMACs
/// under a deployment key: the same client hashes the same within a deployment, which is what
/// "did these sign-ins come from one place" needs, and nothing more.
/// </remarks>
public sealed class HttpAuditRequestInfo(IHttpContextAccessor accessor, ClientHashKey key) : IAuditRequestInfo
{
    private HttpContext? Http => accessor.HttpContext;

    public string? RequestId => Http?.TraceIdentifier;

    public string? Resource => Http is { } http ? $"{http.Request.Method} {http.Request.Path}" : null;

    public byte[]? IpHash => Http?.Connection.RemoteIpAddress is { } ip ? key.Hash("ip:" + ip) : null;

    public byte[]? UserAgentHash => Http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? key.Hash("ua:" + ua) : null;
}

public sealed class ClientHashKey(byte[] key)
{
    public byte[] Hash(string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}

public static class AuditServices
{
    /// <summary>
    /// The audit trail for a host. <paramref name="hasDatabase"/> says whether the host has the
    /// application's <see cref="NpgsqlDataSource"/>; without one (the web host on sample data)
    /// the trail buffers and writes nothing.
    /// </summary>
    public static IServiceCollection AddBrightTimeAudit(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, bool hasDatabase)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        AuditOptions audit = configuration.GetSection(AuditOptions.SectionName).Get<AuditOptions>() ?? new();

        byte[] key;
        if (!string.IsNullOrWhiteSpace(audit.ClientHashKey))
        {
            key = Convert.FromBase64String(audit.ClientHashKey);
            if (key.Length < 32)
            {
                throw new InvalidOperationException("Audit:ClientHashKey must be at least 32 bytes, base64-encoded.");
            }
        }
        else if (hasDatabase && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Audit:ClientHashKey is required: client addresses are recorded only as keyed hashes.");
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
        }

        services.AddHttpContextAccessor();
        services.AddSingleton(new ClientHashKey(key));
        services.AddScoped<IAuditRequestInfo, HttpAuditRequestInfo>();
        services.AddScoped(sp => new AuditTrail(
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IAuditRequestInfo>(),
            sp.GetRequiredService<IClock>(),
            hasDatabase ? sp.GetRequiredService<NpgsqlDataSource>() : null));
        services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditTrail>());
        if (hasDatabase)
        {
            services.AddSingleton(sp => new AuditLog(sp.GetRequiredService<NpgsqlDataSource>()));
        }

        services.AddHostedService<YouthRecordGuard>();
        return services;
    }
}
