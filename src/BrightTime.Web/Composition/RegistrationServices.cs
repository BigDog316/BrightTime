using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Security;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Modules.Enrollment.Payments;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Reporting;

namespace BrightTime.Web.Composition;

/// <summary>
/// The provider console's M3 work (D58): registration, attendance and reports, on the same modules
/// and the same login as the API. Only with a database, as every console page is.
/// </summary>
/// <remarks>
/// The modules run here exactly as they do behind the API: as <c>brighttime_app</c>, under the
/// tenant's RLS and the actor guard, with family data decrypted under the tenant's data key (D32).
/// A console page is one more caller of the same services, not a second implementation.
/// </remarks>
public static class RegistrationServices
{
    public static IServiceCollection AddBrightTimeRegistration(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        FinderOptions bound = configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>() ?? new();
        if (bound.Mode != FinderMode.Sql)
        {
            return services;
        }

        string connection = bound.ConnectionString!;
        services.AddScoped<IListingOccurrences, ListingOccurrences>();
        services.AddFieldEncryption(configuration, environment.IsDevelopment());

        services.AddBrightTimeDbContext<EnrollmentDbContext>(connection, EnrollmentDbContext.SchemaName, EnrollmentDbContext.MigrationsHistoryTable);
        services.AddPaymentGateway(configuration);
        services.AddEnrollment();

        services.AddBrightTimeDbContext<AttendanceDbContext>(connection, AttendanceDbContext.SchemaName, AttendanceDbContext.MigrationsHistoryTable);
        services.AddAttendance();
        services.AddProviderReports(configuration);
        return services;
    }
}
