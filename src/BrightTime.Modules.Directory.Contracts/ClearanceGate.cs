using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Directory.Contracts;

/// <summary>
/// The county's clearance rules (D99), kept as feature flags: one switches enforcement on, and one
/// per kind adds that kind to the platform's minimum.
/// </summary>
public static class ClearanceRuleKeys
{
    /// <summary>On: acting on a named young person's record needs current clearances.</summary>
    public const string Enforced = "clearances.required";

    /// <summary>The prefix of the extra kinds' flags.</summary>
    public const string ExtraPrefix = "clearances.also.";

    /// <summary>The flag that adds <paramref name="kind"/> (a <c>ck_staff_clearance_kind</c> value).</summary>
    public static string Extra(string kind) => ExtraPrefix + kind;
}

/// <summary>What a person lacks to act on young people's records at an organisation.</summary>
/// <param name="Missing">Required kinds not on file, or not passed.</param>
/// <param name="Expired">Required kinds whose expiry has gone.</param>
public sealed record ClearanceRefusal(IReadOnlyList<string> Missing, IReadOnlyList<string> Expired);

/// <summary>
/// Whether someone may act on a named young person's record for an organisation, as far as
/// clearances go (D99, docs/plan/04 <c>ClearanceCurrentRequirement</c>). Null: allowed, because the
/// county doesn't enforce clearances or the person holds every one it requires.
/// </summary>
public interface IClearanceGate
{
    Task<ClearanceRefusal?> CheckAsync(UserId person, OrganisationId organisation, CancellationToken cancellationToken = default);

    /// <summary>
    /// The people who hold a current clearance of every kind the county requires (D129), for at
    /// least one of the organisations given with them: as <see cref="CheckAsync"/> judges it, but
    /// whether or not the county enforces the rules. A mentor is found in search only then.
    /// </summary>
    Task<IReadOnlySet<Guid>> ClearedAsync(IReadOnlyCollection<(Guid Person, Guid Organisation)> people, CancellationToken cancellationToken = default);
}
