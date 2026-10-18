using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Hosting.Auditing;

/// <summary>
/// Writes the request's buffered audit events before its response starts, and holds
/// <see cref="YouthRecordAttribute"/> endpoints to their promise.
/// </summary>
/// <remarks>
/// <para>
/// The flush runs as the response starts, not after it has gone: an audited read must be on
/// record before its data leaves. A flush that fails fails the request.
/// </para>
/// <para>
/// An endpoint declaring <see cref="YouthRecordAttribute"/> that finishes without recording
/// anything gets an <c>error</c> row naming the endpoint and its action. The gap is then in
/// the audit log itself, where a review finds it, rather than nowhere.
/// </para>
/// </remarks>
public sealed partial class AuditFlushMiddleware(RequestDelegate next, ILogger<AuditFlushMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AuditTrail trail, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(tenant);

        context.Response.OnStarting(() => FlushAsync(context, trail, tenant));
        await next(context).ConfigureAwait(false);

        // Responses that never started (an empty 204, a redirect already sent) flush here.
        await FlushAsync(context, trail, tenant).ConfigureAwait(false);
    }

    private async Task FlushAsync(HttpContext context, AuditTrail trail, ITenantContext tenant)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<YouthRecordAttribute>() is { } declared
            && !trail.HasRecorded && tenant.IsResolved)
        {
            LogMissingAudit(logger, declared.Action, context.Request.Path);
            trail.Record(new AuditAccess
            {
                Action = declared.Action,
                Outcome = AuditOutcome.Error,
                Reason = "The endpoint declares [YouthRecord] but recorded no audit event.",
            });
        }

        await trail.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "Endpoint {Path} declares [YouthRecord({Action})] but recorded no audit event.")]
    private static partial void LogMissingAudit(ILogger logger, string action, string path);
}
