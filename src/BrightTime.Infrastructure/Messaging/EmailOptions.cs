namespace BrightTime.Infrastructure.Messaging;

/// <summary>
/// Outbound email. Set <see cref="Host"/> for SMTP (mailpit locally, a relay in production),
/// or <see cref="PickupDirectory"/> to write each message as a .eml file. With neither, mail
/// is not sent and a warning says so.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string? Host { get; set; }

    public int Port { get; set; } = 25;

    public bool EnableSsl { get; set; }

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "BrightTime <no-reply@brighttime.invalid>";

    public string? PickupDirectory { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) || !string.IsNullOrWhiteSpace(PickupDirectory);
}
