using BrightTime.Modules.Directory.Contracts;

namespace BrightTime.Modules.Directory.Clearances;

/// <summary>
/// Whether someone's clearances let them work with young people today, and
/// when that stops being true.
/// </summary>
/// <remarks>
/// Deliberately a pure function over a list of states, so it can be used in
/// three places that must agree: the authorisation check on every youth-facing
/// action, the nightly job that emits <c>ClearanceLapsed</c>, and the
/// organiser dashboard that warns before the lapse happens.
/// </remarks>
public static class ClearanceCurrency
{
    /// <summary>How far ahead the dashboard warns. Long enough to renew a background check.</summary>
    public static readonly int DefaultWarningDays = 45;

    public sealed record Assessment(
        bool IsCleared,
        IReadOnlyList<ClearanceKind> Missing,
        IReadOnlyList<ClearanceKind> Expired,
        IReadOnlyList<(ClearanceKind Kind, DateOnly ExpiresOn)> ExpiringSoon,
        DateOnly? EarliestExpiry);

    public static Assessment Assess(
        IReadOnlyList<ClearanceState> held,
        IReadOnlyList<ClearanceKind> required,
        DateOnly asOf,
        int warningDays = 45)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(required);

        List<ClearanceKind> missing = [];
        List<ClearanceKind> expired = [];
        List<(ClearanceKind, DateOnly)> expiringSoon = [];
        DateOnly? earliest = null;

        foreach (ClearanceKind kind in required)
        {
            ClearanceState? state = held.FirstOrDefault(h => h.Kind == kind);

            if (state is null || state.Status is ClearanceStatus.NotStarted or ClearanceStatus.Pending)
            {
                missing.Add(kind);
                continue;
            }

            if (state.Status is ClearanceStatus.NotCleared or ClearanceStatus.Withdrawn)
            {
                missing.Add(kind);
                continue;
            }

            if (!state.IsCurrentOn(asOf))
            {
                // Status may still read 'cleared' in the row — the expiry date
                // is what decides. An expired clearance is not a stale pass; it
                // is not a pass.
                expired.Add(kind);
                continue;
            }

            if (state.ExpiresOn is { } expiresOn)
            {
                if (earliest is null || expiresOn < earliest)
                {
                    earliest = expiresOn;
                }

                if (expiresOn <= asOf.AddDays(warningDays))
                {
                    expiringSoon.Add((kind, expiresOn));
                }
            }
        }

        return new Assessment(
            missing.Count == 0 && expired.Count == 0,
            missing,
            expired,
            expiringSoon,
            earliest);
    }

    /// <summary>
    /// A message the organisation can act on. Distinguishes "never obtained"
    /// from "lapsed", because they are different conversations with the person
    /// concerned.
    /// </summary>
    public static string Explain(Assessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (assessment.IsCleared)
        {
            return assessment.EarliestExpiry is { } expiry
                ? $"Cleared for youth-facing work. Earliest expiry {expiry:yyyy-MM-dd}."
                : "Cleared for youth-facing work.";
        }

        List<string> parts = [];

        if (assessment.Missing.Count > 0)
        {
            parts.Add($"not on file: {string.Join(", ", assessment.Missing.Select(k => k.ToDbValue()))}");
        }

        if (assessment.Expired.Count > 0)
        {
            parts.Add($"expired: {string.Join(", ", assessment.Expired.Select(k => k.ToDbValue()))}");
        }

        return "Not cleared for youth-facing work — " + string.Join("; ", parts) + ".";
    }
}
