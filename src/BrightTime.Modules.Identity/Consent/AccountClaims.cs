using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Identity.Consent;

/// <summary>
/// Completes a child-to-youth claim (D35), wherever the young person answers their email: the
/// web's <c>/account/claim</c> page, or the app through the API.
/// </summary>
/// <remarks>
/// The account changes hands as <c>brighttime_auth</c> (<see cref="PasswordAccounts.ClaimAsync"/>).
/// Nobody is signed in, so the capabilities that follow from it (no longer under 13, still the
/// guardian's to consent for) are recomputed on the worker, booked here; until the job runs the
/// stored row still holds the stricter child floor.
/// </remarks>
public sealed class AccountClaims(PasswordAccounts passwords, Guardianship guardianship, AuditTrail audit, ITenantContext tenant)
{
    public async Task<Result<UserId>> CompleteAsync(string token, string password, CancellationToken cancellationToken = default)
    {
        Result<UserId> claimed = await passwords.ClaimAsync(tenant.TenantId, token, password, cancellationToken).ConfigureAwait(false);
        if (!claimed.IsSuccess)
        {
            return claimed;
        }

        await guardianship.RequestRecomputeAsync(claimed.Value, "claimed", cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditAccess
        {
            Action = Guardianship.AccountClaimed,
            SubjectKind = "user",
            SubjectIds = [claimed.Value.Value],
        });
        return claimed;
    }
}
