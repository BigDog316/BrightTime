using System.Globalization;
using BrightTime.Abstractions.Screening;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Persistence;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrightTime.Infrastructure.Screening;

/// <summary>
/// Screens what a young person writes in a form, where it first reaches someone else (D118): a
/// project sent for approval, a playlist reflection, a picture's words, their portfolio. Personal
/// information refuses it, saying what to take out, and nothing waits on a moderator.
/// </summary>
/// <remarks>
/// Each check is an audit event in the caller's transaction (<see cref="Checked"/>): allowed or
/// denied, the kinds found as the reason, and which form, never the text. The module's own
/// transaction keeps a refusal's row, since a refused result is returned, not thrown. Fails
/// closed: a screen that errs refuses too.
/// </remarks>
public sealed class WritingScreen(IScreeningService screener, AuditTrail audit)
{
    public const string Checked = "screening.checked";

    /// <summary>
    /// Null when <paramref name="texts"/> share nothing personal; otherwise the refusal,
    /// <c>screening.personal_information</c>, for the form to show. <paramref name="surface"/> is
    /// which form, for the audit row (<c>project</c>, <c>reflection</c>, <c>portfolio</c>), and
    /// <paramref name="subjectKind"/> and <paramref name="subject"/> what it's about.
    /// </summary>
    public async Task<Error?> CheckAsync(
        BrightTimeDbContext db, string surface, string subjectKind, Guid subject, IEnumerable<string?> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(texts);
        string written = string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (written.Length == 0)
        {
            return null;
        }

        ScreeningOutcome outcome;
        try
        {
            outcome = await screener.ScreenTextAsync(written, new ScreeningContext(
                AuthorIsMinor: true, AudienceIncludesMinor: false, AuthorIsAdultAddressingMinor: false, Surface: surface), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            outcome = new ScreeningOutcome(ScreeningDecision.Error, [], new Dictionary<string, double>(StringComparer.Ordinal), null, e.GetType().Name);
        }

        bool passed = outcome.Decision == ScreeningDecision.Pass;
        string kinds = outcome.Decision == ScreeningDecision.Error ? "screening_error" : string.Join(",", outcome.Labels.Keys.Order(StringComparer.Ordinal));
        await audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = Checked,
            Outcome = passed ? AuditOutcome.Allow : AuditOutcome.Deny,
            SubjectKind = subjectKind,
            SubjectIds = [subject],
            Reason = passed ? null : kinds,
            Detail = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["surface"] = surface,
                ["version"] = outcome.ModelVersion ?? "",
                ["spans"] = outcome.PiiSpans.Count.ToString(CultureInfo.InvariantCulture),
            },
        }, ct).ConfigureAwait(false);

        return passed ? null : Error.Validation("screening.personal_information",
            "Take out any phone number, email or home address, social media handle or link before sending this: they can't be shared here.");
    }
}

public static class ScreeningServices
{
    /// <summary>
    /// The local screen (D117) and the form check (D118), once for every host that runs a module
    /// asking for them; a second call changes nothing.
    /// </summary>
    public static IServiceCollection AddLocalScreening(this IServiceCollection services)
    {
        services.TryAddSingleton<IScreeningService, LocalPiiScreen>();
        services.TryAddScoped<WritingScreen>();
        return services;
    }
}
