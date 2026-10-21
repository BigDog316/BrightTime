namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// The role codes from docs/plan/04-roles-and-permissions.md, and the default
/// permission set each one is seeded with.
/// </summary>
/// <remarks>
/// Note what <see cref="PlatformAdmin"/> does <em>not</em> have: any
/// youth-record permission. Platform administration is tenancy, deployment and
/// incident response, not a licence over children's data. Break-glass access
/// is a separate, dual-authorised, time-boxed grant that writes an audit event
/// with a mandatory reason.
/// </remarks>
public static class BrightTimeRoles
{
    public const string Youth = "youth";
    public const string Child = "child";
    public const string Guardian = "guardian";
    public const string ProgramStaff = "program_staff";
    public const string ProviderAdmin = "provider_admin";
    public const string Mentor = "mentor";
    public const string NetworkOrganiser = "network_organiser";
    public const string Moderator = "moderator";
    public const string CivicViewer = "civic_viewer";
    public const string Researcher = "researcher";
    public const string FieldCollector = "field_collector";
    public const string PlatformAdmin = "platform_admin";

    private static readonly string[] PublicBrowse =
        [Permissions.Listings.Search, Permissions.Listings.ViewPublished, Permissions.Organisations.ViewPublic];

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultPermissions { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Youth] =
            [
                .. PublicBrowse,
                Permissions.Listings.Save,
                Permissions.Audit.Read,
            ],

            // A child account browses and nothing else. Everything it can do
            // beyond that is mediated by a guardian.
            [Child] = [.. PublicBrowse],

            [Guardian] =
            [
                .. PublicBrowse,
                Permissions.Listings.Save,
                Permissions.Organisations.Apply,
                Permissions.Audit.Read,
            ],

            [ProgramStaff] =
            [
                .. PublicBrowse,
                Permissions.Listings.Save,
                Permissions.Listings.Create,
                Permissions.Listings.SubmitForReview,
                Permissions.Sites.Manage,
                Permissions.Clearances.ViewStatus,
                Permissions.Youth.ReadRecord,
                Permissions.Applications.View,
                Permissions.Applications.Decide,
            ],

            [ProviderAdmin] =
            [
                .. PublicBrowse,
                Permissions.Listings.Save,
                Permissions.Listings.Create,
                Permissions.Listings.SubmitForReview,
                Permissions.Listings.BulkImport,
                Permissions.Organisations.Edit,
                Permissions.Sites.Manage,
                Permissions.Staff.Invite,
                Permissions.Clearances.Record,
                Permissions.Clearances.ViewStatus,
                Permissions.Youth.ReadRecord,
                Permissions.Youth.ReadSupportNeeds,
                Permissions.Applications.View,
                Permissions.Applications.Decide,
                Permissions.Audit.Read,
            ],

            [Mentor] =
            [
                .. PublicBrowse,
                Permissions.Listings.Save,
                Permissions.Clearances.ViewStatus,
            ],

            [NetworkOrganiser] =
            [
                .. PublicBrowse,
                Permissions.Listings.Save,
                Permissions.Listings.Create,
                Permissions.Listings.SubmitForReview,
                Permissions.Listings.Publish,
                Permissions.Listings.Review,
                Permissions.Listings.BulkImport,
                Permissions.Collections.Manage,
                Permissions.NeighbourhoodPages.Manage,
                Permissions.Organisations.Vet,
                Permissions.Organisations.Approve,
                Permissions.Organisations.Suspend,
                Permissions.Organisations.Edit,
                Permissions.Sites.Manage,
                Permissions.Staff.Invite,
                Permissions.Clearances.Record,
                Permissions.Clearances.ViewStatus,
                Permissions.Consent.AttestSignedForm,
                Permissions.Taxonomy.Manage,
                Permissions.Tenant.Settings,
                Permissions.Tenant.FeatureFlags,
                Permissions.Audit.Read,
                Permissions.SubjectRequests.Fulfil,
            ],

            [Moderator] = [.. PublicBrowse, Permissions.Audit.Read],

            [CivicViewer] = [.. PublicBrowse],

            [Researcher] = [.. PublicBrowse],

            [FieldCollector] = [.. PublicBrowse, Permissions.Listings.Save],

            // Deliberately no youth-record permission. See the remarks above.
            [PlatformAdmin] =
            [
                .. PublicBrowse,
                Permissions.Tenant.Settings,
                Permissions.Tenant.FeatureFlags,
                Permissions.Organisations.Suspend,
                Permissions.Audit.Read,
                Permissions.SubjectRequests.Fulfil,
            ],
        };

    /// <summary>
    /// The roles whose <c>audit.read</c> is tenant-wide (● in the permission matrix). For a
    /// youth, a guardian or a provider admin the same permission is scoped to their own
    /// record, their children's or their organisation's, which the explorer does not serve.
    /// </summary>
    public static IReadOnlyList<string> TenantWideAudit { get; } = [NetworkOrganiser, Moderator, PlatformAdmin];

    public static IReadOnlyList<string> All { get; } =
        [.. DefaultPermissions.Keys];
}
