namespace BrightTime.Modules.Community.Contracts;

/// <summary>
/// Reporting a message (D115): the reasons a person in a conversation can give
/// (<c>ck_report_reason</c>, those that fit a message), and the severity each puts a report in.
/// </summary>
public static class ReportReasons
{
    public const string Harassment = "harassment";
    public const string SexualContent = "sexual_content";
    public const string GroomingConcern = "grooming_concern";
    public const string SelfHarm = "self_harm";
    public const string Violence = "violence";
    public const string HateSpeech = "hate_speech";
    public const string PersonalInformation = "personal_information";
    public const string Spam = "spam";
    public const string SafetyOther = "safety_other";

    /// <summary>The reasons for a message, in the order a reporter is offered them.</summary>
    public static IReadOnlyList<string> ForMessages { get; } =
        [GroomingConcern, SelfHarm, SexualContent, Violence, Harassment, HateSpeech, PersonalInformation, Spam, SafetyOther];

    /// <summary>
    /// The severity a reason puts a report in, whoever reports it: <c>grooming_concern</c> and
    /// <c>self_harm</c> are critical (the comment on <c>ck_report_reason</c>, docs/plan/07).
    /// </summary>
    public static string SeverityOf(string reason) => reason switch
    {
        GroomingConcern or SelfHarm => ReportSeverities.Critical,
        SexualContent or Violence or Harassment or HateSpeech => ReportSeverities.High,
        _ => ReportSeverities.Normal,
    };
}

/// <summary>A report's severity (<c>ck_report_severity</c>).</summary>
public static class ReportSeverities
{
    public const string Normal = "normal";
    public const string High = "high";
    public const string Critical = "critical";

    /// <summary>The queue's order: critical first.</summary>
    public static int Rank(string severity) => severity switch
    {
        Critical => 0,
        High => 1,
        _ => 2,
    };

    /// <summary>A critical report is acknowledged within this long (the comment on <c>ck_report_reason</c>).</summary>
    public static readonly TimeSpan CriticalAcknowledgement = TimeSpan.FromHours(1);
}

/// <summary>A report's states (<c>ck_report_state</c>). Open and triaged are the queue.</summary>
public static class ReportStates
{
    public const string Open = "open";
    public const string Triaged = "triaged";
    public const string Actioned = "actioned";
    public const string Dismissed = "dismissed";
    public const string Escalated = "escalated";
}

/// <summary>
/// The email job a critical report books for each of the county's moderators (D115). It names the
/// report and nothing of the message: the moderator reads that in the console.
/// </summary>
/// <param name="RecipientUserId">A moderator or network organiser of the tenant.</param>
/// <param name="ReportId">The report.</param>
/// <param name="Reason">The report's reason, for the email's wording.</param>
public sealed record CriticalReportNotice(Guid RecipientUserId, Guid ReportId, string Reason)
{
    public const string JobType = "community.critical_report_email";

    /// <summary>One email per moderator and report, however often the job is retried.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UniqueKey => $"{JobType}:{RecipientUserId:N}:{ReportId:N}";
}

/// <summary>
/// A critical report from someone not signed in (D128). A visitor can't read who the moderators
/// are (they sit behind the actor guard), so the report books this one job, and the worker emails
/// each moderator who may decide it: not a member of <paramref name="OrgId"/> (empty for a
/// portfolio, which is of none).
/// </summary>
public sealed record CriticalPublicReport(Guid ReportId, string Reason, Guid OrgId)
{
    public const string JobType = "community.critical_public_report";

    [System.Text.Json.Serialization.JsonIgnore]
    public string UniqueKey => $"{JobType}:{ReportId:N}";
}
