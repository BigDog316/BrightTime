using System.Globalization;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Npgsql;

namespace BrightTime.Modules.Identity.SignIn;

/// <summary>A passkey as its owner sees it on the security page.</summary>
public sealed record PasskeySummary(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool IsBackedUp);

/// <summary>
/// Passkeys (WebAuthn): registering one for a signed-in account, and signing in with one.
/// The threat model prefers them, for youth especially: nothing to phish, nothing reused
/// from another site (T21).
/// </summary>
/// <remarks>
/// <para>
/// Discoverable credentials with user verification required: the authenticator itself
/// knows which account a passkey is for, so sign-in never asks for a username and cannot
/// be used to test whether one exists.
/// </para>
/// <para>
/// Credentials live in <c>identity.user_credential</c> and are read and written as
/// <c>brighttime_auth</c>. That table has no tenant of its own; its row-level security
/// derives from the owning account's, so a credential from another tenant is simply not
/// found.
/// </para>
/// <para>
/// A passkey sign-in is not refused by a password lockout: lockout answers password
/// guessing, and a passkey cannot be guessed. An inactive, deleted or child account still
/// cannot sign in.
/// </para>
/// </remarks>
public sealed class Passkeys(IFido2 fido2, NpgsqlDataSource authDataSource, IClock clock)
{
    public const int MaxNameLength = 60;

    /// <summary>Registration options for a signed-in account, excluding the passkeys it already has.</summary>
    public async Task<CredentialCreateOptions> BeginRegistrationAsync(TenantId tenant, UserId user, string userName, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<byte[]> existing = await CredentialIdsAsync(tenant, user, cancellationToken).ConfigureAwait(false);

        return fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(user), Name = userName, DisplayName = userName },
            ExcludeCredentials = [.. existing.Select(id => new PublicKeyCredentialDescriptor(id))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
    }

    /// <summary>Verifies the authenticator's answer and stores the new passkey.</summary>
    public async Task<Result> CompleteRegistrationAsync(
        TenantId tenant,
        UserId user,
        CredentialCreateOptions original,
        AuthenticatorAttestationRawResponse response,
        string? name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(response);

        // The options must have been made for this account: a registration started by one
        // person cannot be finished into another's.
        if (!original.User.Id.AsSpan().SequenceEqual(UserHandle(user)))
        {
            return Error.Forbidden("passkey.wrong_account", "That passkey request was started by another account.");
        }

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = original,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                {
                    // This tenant's view only; ux_user_credential_cred_id covers the rest
                    // and is caught below.
                    await using NpgsqlCommand check = new(
                        "select not exists (select 1 from identity.user_credential where credential_id = @id)", connection, tx);
                    check.Parameters.Add(new NpgsqlParameter("id", args.CredentialId));
                    return await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            return Error.Validation("passkey.invalid", "That passkey could not be verified: " + ex.Message);
        }

        string label = string.IsNullOrWhiteSpace(name) ? "Passkey" : name.Trim();
        await using (NpgsqlCommand insert = new(
            """
            insert into identity.user_credential
              (id, user_id, credential_id, public_key, signature_counter, aaguid, transports, attestation_fmt,
               is_backed_up, is_user_verifying, friendly_name, created_at)
            values (@id, @user, @cred, @key, @counter, @aaguid, @transports, @fmt, @backed, true, @name, @now)
            """, connection, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter("id", UuidV7.NewId()));
            insert.Parameters.Add(new NpgsqlParameter("user", user.Value));
            insert.Parameters.Add(new NpgsqlParameter("cred", credential.Id));
            insert.Parameters.Add(new NpgsqlParameter("key", credential.PublicKey));
            insert.Parameters.Add(new NpgsqlParameter("counter", (long)credential.SignCount));
            insert.Parameters.Add(new NpgsqlParameter("aaguid", credential.AaGuid));
            insert.Parameters.Add(new NpgsqlParameter("transports",
                (credential.Transports ?? []).Select(t => t.ToString().ToLowerInvariant()).ToArray()));
            insert.Parameters.Add(new NpgsqlParameter("fmt", credential.AttestationFormat ?? "none"));
            insert.Parameters.Add(new NpgsqlParameter("backed", credential.IsBackedUp));
            insert.Parameters.Add(new NpgsqlParameter("name", label.Length > MaxNameLength ? label[..MaxNameLength] : label));
            insert.Parameters.Add(new NpgsqlParameter("now", clock.UtcNow));
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Error.Conflict("passkey.exists", "That passkey is already registered.");
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Step-up options: only this account's passkeys are allowed, and the caller still checks
    /// that the answer came from this account.
    /// </summary>
    public async Task<AssertionOptions> BeginConfirmAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<byte[]> ids = await CredentialIdsAsync(tenant, user, cancellationToken).ConfigureAwait(false);
        return fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. ids.Select(id => new PublicKeyCredentialDescriptor(id))],
            UserVerification = UserVerificationRequirement.Required,
        });
    }

    public async Task<bool> HasPasskeysAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default) =>
        (await CredentialIdsAsync(tenant, user, cancellationToken).ConfigureAwait(false)).Count > 0;

    private async Task<IReadOnlyList<byte[]>> CredentialIdsAsync(TenantId tenant, UserId user, CancellationToken ct)
    {
        List<byte[]> ids = [];
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, ct).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            "select credential_id from identity.user_credential where user_id = @u and revoked_at is null", connection, tx);
        command.Parameters.Add(new NpgsqlParameter("u", user.Value));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(await reader.GetFieldValueAsync<byte[]>(0, ct).ConfigureAwait(false));
        }

        return ids;
    }

    /// <summary>Sign-in options: any of the person's passkeys, chosen by their device.</summary>
    public AssertionOptions BeginSignIn() =>
        fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

    /// <summary>The account a valid assertion proves, or null for any failure.</summary>
    public async Task<UserId?> CompleteSignInAsync(
        TenantId tenant,
        AssertionOptions original,
        AuthenticatorAssertionRawResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(response);

        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);

        Stored? stored = await FindAsync(connection, tx, response.RawId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !stored.CanSignIn)
        {
            return null;
        }

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = original,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = (uint)stored.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(UserHandle(new UserId(stored.UserId)))),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException)
        {
            // Includes a signature counter that went backwards: a sign of a cloned authenticator.
            return null;
        }

        DateTimeOffset now = clock.UtcNow;
        await using (NpgsqlCommand update = new(
            """
            update identity.user_credential set signature_counter = @counter, last_used_at = @now, is_backed_up = @backed where id = @id;
            update identity.app_user set last_login_at = @now where id = @user;
            """, connection, tx))
        {
            update.Parameters.Add(new NpgsqlParameter("counter", (long)result.SignCount));
            update.Parameters.Add(new NpgsqlParameter("now", now));
            update.Parameters.Add(new NpgsqlParameter("backed", result.IsBackedUp));
            update.Parameters.Add(new NpgsqlParameter("id", stored.Id));
            update.Parameters.Add(new NpgsqlParameter("user", stored.UserId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserId(stored.UserId);
    }

    public async Task<IReadOnlyList<PasskeySummary>> ListAsync(TenantId tenant, UserId user, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            select id, coalesce(friendly_name, 'Passkey'), created_at, last_used_at, is_backed_up
              from identity.user_credential
             where user_id = @u and revoked_at is null
             order by created_at
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("u", user.Value));

        List<PasskeySummary> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PasskeySummary(
                reader.GetGuid(0),
                reader.GetString(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false),
                reader.GetBoolean(4)));
        }

        return rows;
    }

    /// <summary>Revokes one of the account's own passkeys. Revoked, not deleted: the row is the audit trail.</summary>
    public async Task<bool> RevokeAsync(TenantId tenant, UserId user, Guid passkeyId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await authDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await BeginAsync(connection, tenant, cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            "update identity.user_credential set revoked_at = @now where id = @id and user_id = @u and revoked_at is null", connection, tx);
        command.Parameters.Add(new NpgsqlParameter("now", clock.UtcNow));
        command.Parameters.Add(new NpgsqlParameter("id", passkeyId));
        command.Parameters.Add(new NpgsqlParameter("u", user.Value));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    /// <summary>The WebAuthn user handle: the account id's sixteen bytes. Opaque, and no personal data.</summary>
    public static byte[] UserHandle(UserId user) => user.Value.ToByteArray(bigEndian: true);

    private sealed record Stored(Guid Id, Guid UserId, byte[] PublicKey, long SignatureCounter, string Status, string Kind)
    {
        public bool CanSignIn => Status == "active" && Kind != "child";
    }

    private static async Task<Stored?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction tx, byte[] credentialId, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(
            """
            select c.id, c.user_id, c.public_key, c.signature_counter, u.status, u.account_kind
              from identity.user_credential c
              join identity.app_user u on u.id = c.user_id and u.deleted_at is null
             where c.credential_id = @id and c.revoked_at is null
               for update of c
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("id", credentialId ?? []));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Stored(
            reader.GetGuid(0),
            reader.GetGuid(1),
            await reader.GetFieldValueAsync<byte[]>(2, ct).ConfigureAwait(false),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenant, CancellationToken ct)
    {
        NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand set = new("select set_config(@key, @value, true)", connection, tx);
        set.Parameters.Add(new NpgsqlParameter("key", SessionVariables.TenantId));
        set.Parameters.Add(new NpgsqlParameter("value", tenant.Value.ToString("D", CultureInfo.InvariantCulture)));
        await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return tx;
    }
}
