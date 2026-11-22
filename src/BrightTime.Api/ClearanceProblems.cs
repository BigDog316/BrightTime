using BrightTime.Hosting.Auth;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api;

/// <summary>
/// Says why the county's clearance rules (D99) refused a request. Each endpoint refuses in its own
/// words ("your role doesn't cover that"); where the clearance gate is what refused, this replaces
/// the 403 with <c>clearances.not_current</c> and the kinds missing and expired, so the app can tell
/// a staff member what to put right instead of that their role is wrong.
/// </summary>
/// <remarks>
/// Only a 403, so a not-found that hides a record stays one. The gate leaves a refusal only where
/// the permission and role would otherwise pass, so the reason is the real one.
/// </remarks>
public static class ClearanceProblems
{
    public const string Code = "clearances.not_current";

    /// <summary>Every endpoint under <paramref name="group"/>.</summary>
    public static RouteGroupBuilder WithClearanceReasons(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.AddEndpointFilter(async (context, next) =>
        {
            object? result = await next(context).ConfigureAwait(false);
            return result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status403Forbidden } && RefusalOf(context.HttpContext) is { } refusal
                ? Problem(refusal)
                : result;
        });
        return group;
    }

    /// <summary>The problem for <paramref name="refusal"/>: the kinds as arrays of codes, for the reader's language to name.</summary>
    public static IResult Problem(ClearanceRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return ApiProblems.From(Error.Forbidden(Code,
            "The county requires current clearances to act on young people's records here. An owner or admin can record them.",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["missing"] = refusal.Missing.ToArray(),
                ["expired"] = refusal.Expired.ToArray(),
            }));
    }

    /// <summary>The refusal at the organisation in the route, or the only one the request met.</summary>
    private static ClearanceRefusal? RefusalOf(HttpContext context)
    {
        if (context.Request.RouteValues.TryGetValue("orgId", out object? value) && Guid.TryParse(value?.ToString(), out Guid org))
        {
            return ClearanceGateHandler.RefusalAt(context, org);
        }

        return context.Items[ClearanceGateHandler.ItemKey] is Dictionary<Guid, ClearanceRefusal> { Count: 1 } refusals ? refusals.Values.Single() : null;
    }
}
