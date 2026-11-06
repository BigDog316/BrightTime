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

/// <summary>
/// The family names an account is known by may have changed (D80): a member was added to or
/// changed in a household it owns, or a member it is was linked to it or changed. A portfolio it
/// shows is checked again, so a family name that arrives after showing doesn't stay on the page.
/// Carries the account and nothing else; the names stay sealed in Enrollment.
/// </summary>
public sealed record FamilyNamesChanged(Guid UserId) : IModuleMessage
{
    public static string MessageType => "enrollment.family.names_changed";
}
