using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Abstractions.Messaging;

/// <summary>
/// An outbound SMS. Capped at 320 characters and deliberately detail-free:
/// <c>notify.template</c> enforces both in the schema
/// (<c>ck_template_sms_length</c>, <c>ck_template_sms_no_youth_name</c>). SMS
/// carries a prompt and a link, never a child's name or a program detail.
/// </summary>
public sealed record SmsMessage(string ToE164, string Body, Locale Locale)
{
    public const int MaxBodyLength = 320;
}

public interface ISmsSender
{
    Task<DeliveryReceipt> SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}
