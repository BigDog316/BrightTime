using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Messaging;

namespace BrightTime.Hosting.Mail;

public static class MailServices
{
    /// <summary>Outbound mail from <c>Email:*</c> (<see cref="EmailOptions"/>), through <see cref="SmtpEmailSender"/>.</summary>
    public static IServiceCollection AddBrightTimeEmail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }

    /// <summary>
    /// The <see cref="AccountMailDispatcher"/>, run as a hosted service. Needs the sign-in path's
    /// <c>PasswordAccounts</c> and <c>AccountProvisioning</c>, <see cref="Localisation.TextServices.AddBrightTimeText"/>,
    /// the tenant context and the audit trail.
    /// </summary>
    public static IServiceCollection AddAccountMail(this IServiceCollection services)
    {
        services.AddSingleton<AccountMailDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<AccountMailDispatcher>());
        return services;
    }
}
