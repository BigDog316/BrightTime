using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using Npgsql;

namespace BrightTime.Modules.Platform.Tenancy;

/// <summary>A tenant as a request needs it: who, whether it is open, and its own address.</summary>
public sealed record TenantSite(
    TenantId TenantId,
    string DisplayName,
    string Status,
    string TimeZone,
    string DefaultLocale,
    string PrimaryHost)
{
    public bool IsActive => Status == "active";
}

/// <summary>What a request's host resolved to.</summary>
public sealed record HostResolution(TenantSite Site, bool IsPrimaryHost);

public interface ITenantLookup
{
    /// <summary>The tenant a normalised host belongs to, or null. See <see cref="HostNames.Normalise"/>.</summary>
    Task<HostResolution?> FindByHostAsync(string host, CancellationToken cancellationToken = default);

    Task<TenantSite?> FindByIdAsync(TenantId tenant, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves tenants from <c>platform.tenant_domain</c>, before any tenant context exists.
/// </summary>
/// <remarks>
/// Plain SQL rather than a module context: the contexts refuse to run without a tenant, which
/// is the point of them, and this is the one query that runs before there is one. The domain
/// table is readable without a tenant (policy <c>tenant_domain_lookup</c>; it holds no personal
/// data). The tenant row is not, so it is read with the resolved tenant set, under the same
/// policy as every other request.
/// </remarks>
public sealed class SqlTenantLookup(NpgsqlDataSource dataSource) : ITenantLookup
{
    public async Task<HostResolution?> FindByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid tenantId;
        bool isPrimary;
        await using (NpgsqlCommand domain = new(
            "select tenant_id, is_primary from platform.tenant_domain where host = @host", connection, tx))
        {
            domain.Parameters.Add(new NpgsqlParameter("host", host));
            await using NpgsqlDataReader reader = await domain.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            tenantId = reader.GetGuid(0);
            isPrimary = reader.GetBoolean(1);
        }

        TenantSite? site = await ReadTenantAsync(connection, tx, new TenantId(tenantId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return site is null ? null : new HostResolution(site, isPrimary);
    }

    public async Task<TenantSite?> FindByIdAsync(TenantId tenant, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        TenantSite? site = await ReadTenantAsync(connection, tx, tenant, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return site;
    }

    private static async Task<TenantSite?> ReadTenantAsync(NpgsqlConnection connection, NpgsqlTransaction tx, TenantId tenant, CancellationToken ct)
    {
        await using (NpgsqlCommand set = new("select set_config(@key, @value, true)", connection, tx))
        {
            set.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
            set.Parameters.Add(new NpgsqlParameter("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using NpgsqlCommand command = new(
            """
            select t.display_name, t.status, t.timezone, t.default_locale,
                   (select d.host::text from platform.tenant_domain d where d.tenant_id = t.id and d.is_primary)
              from platform.tenant t
             where t.id = @id
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("id", tenant.Value));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        // A tenant with no primary domain cannot be reached by host, and has no address of its own.
        string? primary = await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4);
        return primary is null
            ? null
            : new TenantSite(tenant, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), primary);
    }
}

/// <summary>Host names as <c>platform.tenant_domain</c> stores them.</summary>
public static class HostNames
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>
    /// Lower case, no port, no trailing dot, internationalised names as punycode; null for
    /// anything that is not a plausible DNS name (ck_tenant_domain_host allows a-z, 0-9, '.'
    /// and '-'). An IP-address host never resolves to a tenant.
    /// </summary>
    public static string? Normalise(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 300)
        {
            return null;
        }

        string value = host.Trim();
        if (value.StartsWith('['))
        {
            return null; // An IPv6 literal.
        }

        int colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            value = value[..colon];
        }

        value = value.TrimEnd('.');
        if (value.Length == 0)
        {
            return null;
        }

        try
        {
            value = Idn.GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (value.Length > 253 || System.Net.IPAddress.TryParse(value, out _)
            || !value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            return null;
        }

        return value;
    }
}
