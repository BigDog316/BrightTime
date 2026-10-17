namespace BrightTime.SharedKernel.Privacy;

/// <summary>
/// Marks a type that carries a young person's record: anything an endpoint returning it must
/// audit. The web host refuses to start if an endpoint's response graph contains a type
/// marked with this and the endpoint does not declare <see cref="YouthRecordAttribute"/>
/// (docs/plan/04-roles-and-permissions.md, "two cross-cutting rules").
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class YouthDataAttribute : Attribute;

/// <summary>
/// Declares that an endpoint reads or writes youth records and audits it. The endpoint must
/// record an audit event for every request it serves; one that finishes without recording
/// gets an <c>error</c> row, so a forgotten entry is visible rather than silent.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class YouthRecordAttribute(string action) : Attribute
{
    /// <summary>The audit action, e.g. <c>enrollment.roster.read</c>.</summary>
    public string Action { get; } = action;

    /// <summary>The fields read, for the access grant check and the audit row's detail.</summary>
    public string[] Fields { get; init; } = [];
}
