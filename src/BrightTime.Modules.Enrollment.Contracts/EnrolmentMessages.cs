using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Modules.Enrollment.Contracts;

/// <summary>
/// An enrolment now belongs to a young person's own account, or no longer does (D65): the family
/// linked the member to the account, or unlinked them. A portfolio built from the enrolment
/// follows the account.
/// </summary>
public sealed record EnrolmentAccountChanged(Guid EnrollmentId, Guid? YouthUserId) : IModuleMessage
{
    public static string MessageType => "enrollment.enrolment.account_changed";
}
