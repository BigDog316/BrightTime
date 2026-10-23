using BrightTime.Hosting.Localisation;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Attendance.Contracts;
using BrightTime.Modules.Attendance.Persistence;
using BrightTime.Modules.Attendance.Records;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Payments;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Enrollment.Roster;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.Modules.Notifications;
using BrightTime.Modules.Notifications.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Personal;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.SharedKernel.Time;
using Npgsql;

namespace BrightTime.Jobs;

/// <summary>
/// Wires the worker: module contexts on the jobs login, the cross-module queries the handlers
/// need, the job store and runner, and every handler.
/// </summary>
/// <remarks>
/// The connection must log in as a role that is a member of <c>brighttime_jobs</c>. That role is
/// subject to the tenant policy on every table, so each handler sees only its own job's tenant,
/// except on <c>platform.job</c> and <c>platform.outbox_message</c>, where <c>job_worker_claims</c>
/// and <c>outbox_dispatch</c> let it claim and dispatch across tenants.
/// </remarks>
public static class JobsComposition
{
    public const string ConnectionStringName = "Jobs";

    public static IServiceCollection AddBrightTimeWorker(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        string connection = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is required: a login that is a member of brighttime_jobs.");

        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddBrightTimeDbContext<OpportunitiesDbContext>(
            connection, OpportunitiesDbContext.SchemaName, OpportunitiesDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<DirectoryDbContext>(
            connection, DirectoryDbContext.SchemaName, DirectoryDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<PlatformDbContext>(
            connection, PlatformDbContext.SchemaName, PlatformDbContext.MigrationsHistoryTable);

        services.AddScoped<IDirectoryQueries, DirectoryQueries>();
        services.AddScoped<PlatformQueries>();
        services.AddScoped<ITaxonomyQueries>(sp => sp.GetRequiredService<PlatformQueries>());
        services.AddScoped<IGeographyQueries>(sp => sp.GetRequiredService<PlatformQueries>());

        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddSingleton<JobStore>();
        services.Configure<JobRunnerOptions>(configuration.GetSection("Jobs"));
        services.AddSingleton<JobRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
        services.Configure<OutboxOptions>(configuration.GetSection("Outbox"));
        services.AddOutboxDispatch();
        services.AddSingleton<AuditPartitionMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditPartitionMaintenance>());

        services.AddListingProjection();

        // Guardianship: the capability recompute booked for the day a child's age band changes.
        // Its audit rows have no request behind them, so no client hashes.
        services.AddBrightTimeDbContext<IdentityDbContext>(
            connection, IdentityDbContext.SchemaName, IdentityDbContext.MigrationsHistoryTable);
        services.AddScoped(sp => new AuditTrail(
            sp.GetRequiredService<ITenantContext>(), NoAuditRequestInfo.Instance, sp.GetRequiredService<IClock>(), sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditTrail>());
        services.AddSingleton<IAuditRequestInfo>(NoAuditRequestInfo.Instance);
        services.AddGuardianshipJobs();
        // When the terms change, the self-enrolment they allowed waits for agreement again (D37).
        services.AddHostedService<TermsSweep>();
        services.AddScoped<IIdentityQueries, IdentityQueries>();

        // Reminders, and delivering what modules ask to be told: the inbox and pushes. Push tokens are
        // decrypted under the tenant's data key (D32), and pushes are worded from the shared catalogues.
        services.AddReminderJobs();
        services.AddBrightTimeDbContext<NotificationsDbContext>(
            connection, NotificationsDbContext.SchemaName, NotificationsDbContext.MigrationsHistoryTable);
        services.AddFieldEncryption(configuration, environment.IsDevelopment());
        services.AddBrightTimeText(hasDatabase: true);
        services.AddNotificationDelivery(configuration);

        // What the payment provider reports, applied to the payment (D56).
        services.AddBrightTimeDbContext<EnrollmentDbContext>(
            connection, EnrollmentDbContext.SchemaName, EnrollmentDbContext.MigrationsHistoryTable);
        services.AddPaymentJobs();

        // A young person's participation, kept in step with their attendance (D65). The projector
        // reads enrolments, attendance and listings through their contracts, on the jobs login.
        services.AddBrightTimeDbContext<AttendanceDbContext>(
            connection, AttendanceDbContext.SchemaName, AttendanceDbContext.MigrationsHistoryTable);
        services.AddBrightTimeDbContext<LearningDbContext>(
            connection, LearningDbContext.SchemaName, LearningDbContext.MigrationsHistoryTable);
        services.AddScoped<IAttendanceQueries, AttendanceQueries>();
        services.AddScoped<IEnrolmentQueries, EnrolmentQueries>();
        services.AddScoped<IListingOccurrences, ListingOccurrences>();
        services.AddScoped<IListingDetails, SqlListingDetails>();
        services.AddLearningJobs();

        return services;
    }
}
