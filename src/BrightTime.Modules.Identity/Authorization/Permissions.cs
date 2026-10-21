namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// The permission vocabulary, as constants.
/// </summary>
/// <remarks>
/// Roles map to sets of these in <c>identity.app_role_claim</c>, seeded per
/// tenant and editable by a network organiser — but only from this list. A
/// tenant cannot invent a permission the code does not define, and cannot grant
/// a youth-record permission without a scope, because the requirement handler
/// resolves the scope separately.
/// </remarks>
public static class Permissions
{
    public static class Listings
    {
        public const string Search = "listings.search";
        public const string ViewPublished = "listings.view_published";
        public const string Save = "listings.save";
        public const string Create = "listings.create";
        public const string SubmitForReview = "listings.submit_for_review";
        public const string Publish = "listings.publish";
        public const string Review = "listings.review";
        public const string BulkImport = "listings.bulk_import";
    }

    public static class Collections
    {
        public const string Manage = "collections.manage";
    }

    public static class NeighbourhoodPages
    {
        public const string Manage = "neighbourhood_pages.manage";
    }

    public static class Organisations
    {
        public const string ViewPublic = "orgs.view_public";
        public const string Apply = "orgs.apply";
        public const string Edit = "orgs.edit";
        public const string Vet = "orgs.vet";
        public const string Approve = "orgs.approve";
        public const string Suspend = "orgs.suspend";
    }

    public static class Sites
    {
        public const string Manage = "sites.manage";
    }

    public static class Staff
    {
        public const string Invite = "staff.invite";
    }

    public static class Clearances
    {
        public const string Record = "clearances.record";
        public const string ViewStatus = "clearances.view_status";
    }

    public static class Youth
    {
        /// <summary>
        /// Reading a young person's record. Never sufficient on its own: the
        /// requirement handler also resolves a live
        /// <c>identity.youth_access_grant</c> for the organisation.
        /// </summary>
        public const string ReadRecord = "youth_record.read";

        /// <summary>Support needs — health, disability, IEP/504. Needs the grant and a family-profile share.</summary>
        public const string ReadSupportNeeds = "support_needs.read";
    }

    public static class Applications
    {
        /// <summary>An organisation's applications: who applied (as far as their share shows), their answers, the verdict.</summary>
        public const string View = "applications.view";

        /// <summary>Accepting, waitlisting and declining an organisation's applications, and offering freed seats.</summary>
        public const string Decide = "applications.decide";
    }

    public static class Attendance
    {
        /// <summary>Recording and correcting attendance for the organisation's enrolments.</summary>
        public const string Record = "attendance.record";

        /// <summary>Reading a meeting's roster and the organisation's attendance records.</summary>
        public const string ViewRoster = "attendance.view_roster";

        /// <summary>Sending a phone's offline queue of records.</summary>
        public const string OfflineSync = "attendance.offline_sync";
    }

    public static class Messages
    {
        /// <summary>
        /// Opening and writing conversations with the families of the organisation's enrolments and
        /// applications (D53). There is deliberately no permission to message a young person alone:
        /// docs/plan/04's <c>messages.send_to_youth_1to1</c> is a permanent never, with no row to grant.
        /// </summary>
        public const string SendToFamily = "messages.send_to_family";
    }

    public static class Consent
    {
        /// <summary>
        /// Recording that a guardian's signed consent form was reviewed in person, which verifies
        /// their link to a child (Q3's fallback). Network organisers only in M2: a provider
        /// attesting for any child in the tenant is too broad until enrolment gives it a scope.
        /// </summary>
        public const string AttestSignedForm = "consent.attest_signed_form";
    }

    public static class Taxonomy
    {
        public const string Manage = "taxonomy.manage";
    }

    public static class Tenant
    {
        public const string Settings = "tenant.settings";
        public const string FeatureFlags = "tenant.feature_flags";
    }

    public static class Audit
    {
        public const string Read = "audit.read";
    }

    public static class SubjectRequests
    {
        public const string Fulfil = "subject_request.fulfil";
    }

    /// <summary>
    /// Every permission the code defines. A role claim naming anything outside
    /// this set is rejected at seed time rather than silently granting nothing.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Listings.Search, Listings.ViewPublished, Listings.Save, Listings.Create,
        Listings.SubmitForReview, Listings.Publish, Listings.Review, Listings.BulkImport,
        Collections.Manage,
        NeighbourhoodPages.Manage,
        Organisations.ViewPublic, Organisations.Apply, Organisations.Edit,
        Organisations.Vet, Organisations.Approve, Organisations.Suspend,
        Sites.Manage,
        Staff.Invite,
        Clearances.Record, Clearances.ViewStatus,
        Youth.ReadRecord, Youth.ReadSupportNeeds,
        Applications.View, Applications.Decide,
        Attendance.Record, Attendance.ViewRoster, Attendance.OfflineSync,
        Messages.SendToFamily,
        Consent.AttestSignedForm,
        Taxonomy.Manage,
        Tenant.Settings, Tenant.FeatureFlags,
        Audit.Read,
        SubjectRequests.Fulfil,
    };
}
