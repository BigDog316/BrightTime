using System.Text.Json;

namespace BrightTime.Ui.Shared.Api;

// The API's shapes as the app reads them (docs/plan/05). Written out here, not shared with the
// server, because the server's types live in its host; the API changes additively, so extra
// fields are ignored and every record tolerates what it does not know. ApiContractTests checks
// these against the running API.

/// <summary>A sentence to render from the catalogue: a key and its arguments (D30's "why").</summary>
public sealed record Phrase(string Key, IReadOnlyDictionary<string, JsonElement>? Args);

/// <summary>A page of results. Pass <see cref="NextCursor"/> back as <c>cursor</c> for the next one.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, IReadOnlyList<Phrase>? Notices);

public sealed record OrganisationRef(Guid Id, string Slug, string Name);

public sealed record Cost(bool IsFree, int? FromCents);

public sealed record ListingSummary(
    Guid Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    OrganisationRef? Organisation,
    Cost Cost,
    DateTimeOffset? NextStartAt,
    DateTimeOffset? NextDeadlineAt,
    double? DistanceMetres,
    IReadOnlyList<Phrase> Why);

public sealed record Place(
    string Name,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? Neighbourhood,
    string? TransitNote,
    bool? IsWheelchairAccessible);

public sealed record Session(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record Deadline(string Kind, DateTimeOffset DueAt, string? Label);

public sealed record ListingDetail(
    Guid Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    string? Description,
    string ContentLanguage,
    string DescriptionLanguage,
    bool IsMachineTranslated,
    OrganisationRef Organisation,
    int? AgeMin,
    int? AgeMax,
    int? GradeMin,
    int? GradeMax,
    string CostModel,
    int? CostCentsMin,
    int? CostCentsMax,
    string? SlidingScaleNote,
    string Modality,
    string RegistrationMode,
    string? ExternalUrl,
    bool MealsProvided,
    bool TransportProvided,
    string? TransportNote,
    bool IsDropIn,
    string? AccessibilityNote,
    string? ContactEmail,
    string? ContactPhone,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Accommodations,
    IReadOnlyList<Place> Places,
    IReadOnlyList<Session> Upcoming,
    IReadOnlyList<Deadline> Deadlines);

public sealed record Coordinate(double Longitude, double Latitude);

public sealed record NeighbourhoodSummary(string Code, string Name, int ListingCount, Coordinate Centre);

public sealed record Term(string Code, string Name, string? ParentCode, int SortOrder);

public sealed record LocaleInfo(string Code, string NativeName, string Direction);

/// <summary>The region the app serves: its name, languages and time zone (<c>GET /public/tenant</c>).</summary>
public sealed record TenantInfo(string Name, string DefaultLocale, string TimeZone, IReadOnlyList<LocaleInfo> Locales);

public sealed record TokenPair(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt)
{
    /// <summary>Never the tokens: a logged pair must not carry them.</summary>
    public override string ToString() => $"TokenPair {{ ExpiresIn = {ExpiresIn} }}";
}

public sealed record MeOrganisation(Guid OrganisationId, string Role);

// ---- attendance (D49, D51, D52) --------------------------------------------------------------

/// <summary>One meeting of one of an organisation's listings: a roster a staff member can take.</summary>
public sealed record AttendanceMeeting(Guid ListingId, string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>A roster line: the enrolment, and a display name only where the family's share names the young person.</summary>
public sealed record AttendanceRosterLine(Guid EnrollmentId, string? DisplayName, Guid? RecordId, string? State);

public sealed record AttendanceRoster(Guid ListingId, DateTimeOffset OccurrenceStartsAt, DateTimeOffset OccurrenceEndsAt, IReadOnlyList<AttendanceRosterLine> Lines);

/// <summary>One mark from the offline queue, with the device's own id for it so a replay is recognised.</summary>
public sealed record AttendanceSyncItem(Guid ClientRecordId, Guid EnrollmentId, DateTimeOffset OccurrenceStartsAt, string State, DateTimeOffset CapturedAt);

/// <summary>What became of one mark: applied, duplicate, conflict or rejected (with the error code).</summary>
public sealed record AttendanceSyncOutcome(Guid ClientRecordId, string Status, Guid? ServerId, string? Reason);

/// <summary>What the server tells a device on contact: whether to wipe, and how long it may stay offline.</summary>
public sealed record AttendanceDeviceContact(bool Registered, bool Wipe, int MaxOfflineHours);

/// <summary>
/// One of the family's current activities (D76): for whom, which, and its meeting under way now or
/// next. <c>MeetingNow</c> runs from an hour before that meeting until it ends.
/// </summary>
public sealed record FamilyActivity(Guid EnrollmentId, string MemberFirstName, Guid ListingId, string ListingTitle, DateTimeOffset? MeetingStartsAt, DateTimeOffset? MeetingEndsAt, bool MeetingNow);

/// <summary>One of the family's attendance records: present, late, absent or excused. Never the staff's note.</summary>
public sealed record FamilyAttendance(Guid EnrollmentId, string MemberFirstName, Guid ListingId, string ListingTitle, DateTimeOffset OccurrenceStartsAt, string State, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt);

/// <summary>
/// A check-in code to show as a QR (D50). It names nobody and works once. The screen counts down
/// <c>ValidForSeconds</c> from when it arrived, not to <c>ExpiresAt</c>, so a phone whose clock is
/// wrong still shows it for as long as it works.
/// </summary>
public sealed record CheckinCode(string Token, DateTimeOffset ExpiresAt, int ValidForSeconds);

/// <summary>
/// One thing a young person took part in (D65, D77), built from attendance. A hidden record stays
/// in their own view, marked, and leaves their total and anything they've shown. <c>ListingId</c>
/// is the activity a project can be about (D78); a record imported from elsewhere has none.
/// </summary>
public sealed record ParticipationView(
    Guid Id, string Title, string? Organisation, DateOnly StartedOn, DateOnly? EndedOn, int SessionsAttended, decimal Hours, string Source, bool IsHidden,
    Guid? ListingId = null);

/// <summary>
/// A young person's own portfolio (D65, D77): how it appears, who may see it
/// (<c>private</c>, <c>link_only</c>, <c>public</c>), and everything in it. <c>CanShow</c> is the
/// server's answer to whether they may show it at all (nobody under 13).
/// </summary>
public sealed record PortfolioView(
    string? DisplayName, string? Headline, string? Bio, string Visibility, string? PublicSlug, decimal TotalHours, bool CanShow,
    IReadOnlyList<ParticipationView> Participation);

/// <summary>What the young person sets: their display name, what they write, who sees it, and its link name.</summary>
public sealed record PortfolioDraft(string DisplayName, string? Headline, string? Bio, string Visibility, string? PublicSlug);

/// <summary>
/// A badge the young person holds (D66, D77). <c>Page</c> is the web page anyone can check it on;
/// the credential itself isn't read by the app.
/// </summary>
public sealed record HeldBadge(Guid Id, string Name, string Issuer, DateTimeOffset IssuedOn, DateTimeOffset? ExpiresOn, bool Revoked, Uri Page);

/// <summary>A skill or interest from the county's lists (D78), by the id the goal and skill routes take.</summary>
public sealed record LearningTerm(Guid Id, string Vocabulary, string Name, Guid? ParentId);

/// <summary>
/// A young person's goal (D69, D78): theirs alone, never shown or exported. <c>Status</c> is
/// <c>active</c>, <c>achieved</c>, <c>paused</c> or <c>abandoned</c>; <c>Target</c> names the
/// skill or interest it's about.
/// </summary>
public sealed record GoalView(
    Guid Id, string Title, string? Detail, Guid? TargetTermId, string? Target, DateOnly? TargetDate, string Status, DateTimeOffset CreatedAt, DateTimeOffset? AchievedAt);

/// <summary>A goal as written or changed. A null status leaves it where it stands.</summary>
public sealed record GoalDraft(string Title, string? Detail, Guid? TargetTermId, DateOnly? TargetDate, string? Status);

/// <summary>
/// A skill on the portfolio (D69, D78): one the young person listed, at a level from 1 to 4 they
/// chose, or one an organisation endorsed (<c>EndorsedBy</c>). The same skill can be both.
/// </summary>
public sealed record SkillView(Guid Id, Guid TermId, string Name, short? Level, bool Endorsed, string? EndorsedBy, DateTimeOffset AssertedAt);

/// <summary>
/// A project (D69, D78): <c>draft</c>, <c>submitted</c> to the organisation of its activity,
/// <c>approved</c>, then <c>published</c> (<c>link_only</c> or <c>public</c>). A change makes it a
/// private draft again.
/// </summary>
public sealed record ProjectView(
    Guid Id, string Title, string? Description, Guid? ListingId, string? Activity, string? Organisation,
    string Status, string Visibility, string? ReviewNote, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt);

/// <summary>A project as written or changed: what it is, and the activity it came from.</summary>
public sealed record ProjectDraft(string Title, string? Description, Guid? ListingId);

/// <summary>
/// One step of a playlist (D67): <c>attend_listing</c>, <c>external_activity</c>, <c>reflection</c>
/// or <c>mentor_checkin</c>. A step to attend names its activity, with its slug while it's published (D79).
/// </summary>
public sealed record PlaylistStep(
    Guid Id, int Order, string Kind, string Title, string? Instructions, Guid? ListingId, string? ExternalUrl, short? ExpectedMinutes,
    bool IsRequired, bool RequiresReview, string? ListingTitle = null, string? ListingSlug = null);

/// <summary>A published playlist, as anyone reads it (<c>GET /public/playlists</c>).</summary>
public sealed record PlaylistSummary(
    Guid Id, string Slug, string Title, string? Description, string? Organisation, bool IsOrdered, short? AgeMin, short? AgeMax,
    decimal? EstimatedHours, string? CompletionBadge, Guid? CompletionBadgeClassId, string? RequiresBadge, IReadOnlyList<PlaylistStep> Steps);

/// <summary>
/// Where a young person is on one step: <c>not_started</c>, <c>submitted</c> (waiting for staff),
/// <c>needs_rework</c> (sent back, with a note) or <c>completed</c>; and whether it's open to them yet.
/// </summary>
public sealed record MyStep(PlaylistStep Step, string State, bool Available, string? Reflection, string? ReviewNote, DateTimeOffset? CompletedAt)
{
    /// <summary>Waiting on the young person: never done, or sent back.</summary>
    public bool IsOpen => State is "not_started" or "needs_rework";
}

/// <summary>A playlist the young person started (<c>GET /me/playlists</c>), and the badge it gave them once complete.</summary>
public sealed record MyPlaylist(
    Guid PlaylistId, string Slug, string Title, string? Organisation, bool IsOrdered, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    Guid? AwardedAssertionId, IReadOnlyList<MyStep> Steps, string? Description = null, string? CompletionBadge = null);

/// <summary>The signed-in person: who, what kind of account (<c>adult</c>, <c>youth</c>), and what they may do.</summary>
public sealed record Me(
    Guid Id,
    string UserName,
    string AccountKind,
    string? Locale,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<MeOrganisation> Organisations)
{
    public bool IsYouth => AccountKind == "youth";

    public bool IsAdult => AccountKind == "adult";
}

/// <summary>
/// What the account may do (<c>GET /me/capabilities</c>): the COPPA gate, decided on the server.
/// The app shows and hides by this, never by its own reading of age or consent.
/// </summary>
public sealed record Capabilities(
    bool IsUnder13,
    bool HasAccountConsent,
    bool CanSelfEnroll,
    bool CanPostInCommunity,
    bool CanUploadMedia,
    bool CanPublishShowcase,
    bool CanReceiveSms,
    bool CanBeMessagedByOrganisation);

public sealed record SavedItem(ListingSummary Listing, string? Note, DateTimeOffset SavedAt);

public sealed record Interest(string Code, string Name, int Weight);

public sealed record SearchPreferences(string? NearNeighbourhood, int? MaxDistanceMetres);

public sealed record Reminder(Guid Id, Guid ListingId, string Anchor, int LeadMinutes, DateTimeOffset RemindAt, bool Sent);

public sealed record InboxItem(Guid Id, string Kind, string Title, string Body, string? DeepLink, DateTimeOffset CreatedAt, bool IsRead);

public sealed record ChannelChoice(string Kind, string Channel, bool Enabled);

public sealed record NotificationSettings(string? QuietFrom, string? QuietTo, IReadOnlyList<ChannelChoice> Choices);

public sealed record GuardianLink(Guid Id, string Relationship, string Status, string? VerificationMethod, DateTimeOffset? VerifiedAt);

public sealed record GivenConsent(string Scope, string Method, DateTimeOffset GrantedAt);

/// <summary>A guardian's child, or a young person they still consent for once it is claimed (<c>accountKind: youth</c>).</summary>
public sealed record Child(
    Guid Id,
    string GivenName,
    string BirthMonth,
    string AccountKind,
    string Status,
    GuardianLink Link,
    IReadOnlyList<GivenConsent> Consents,
    Capabilities Capabilities)
{
    public bool IsClaimed => AccountKind == "youth";
}

/// <summary>A household's details, the people in it and its contacts (D43, D71).</summary>
public sealed record Household(
    Guid Id, short? HouseholdSize, string? PrimaryLanguage, bool NeedsInterpreter, IReadOnlyList<HouseholdMember> Members, IReadOnlyList<HouseholdContact> Contacts);

/// <summary>One person in a household. <see cref="AccountUserId"/> is the account a young member is linked to (D65).</summary>
public sealed record HouseholdMember(
    Guid Id, string Role, string FirstName, string LastName, string? PreferredName, DateOnly? DateOfBirth, short? Grade,
    string? Pronouns, string? Relationship, Guid? NeighbourhoodId, IReadOnlyList<SupportNeed> SupportNeeds, Guid? AccountUserId = null)
{
    public bool IsYoungPerson => Role is "youth" or "child";

    public string ShownName => string.IsNullOrWhiteSpace(PreferredName) ? FirstName : PreferredName;
}

public sealed record SupportNeed(Guid Id, string Kind, string? Detail, bool ShareWithProviders);

public sealed record HouseholdContact(Guid Id, string Name, string Relationship, string Phone, string? Email, bool IsEmergency, bool CanPickUp);

/// <summary>What a household lets one organisation see, and until when (D44).</summary>
public sealed record HouseholdShare(
    Guid Id, Guid OrganisationId, string OrganisationName, Guid? ListingId, IReadOnlyList<Guid> MemberIds, IReadOnlyList<string> Fields,
    DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string Status)
{
    public bool IsActive => Status == "active";
}

/// <summary>A member as the form sends it.</summary>
public sealed record MemberDraft(
    string Role, string FirstName, string LastName, string? PreferredName, DateOnly? DateOfBirth, short? Grade, string? Pronouns, string? Relationship,
    Guid? NeighbourhoodId);

public sealed record ContactDraft(string Name, string Relationship, string Phone, string? Email, bool IsEmergency, bool CanPickUp);

public sealed record SupportNeedDraft(string Kind, string? Detail, bool ShareWithProviders);

/// <summary>One of the family's applications, as the list shows it (D46, D72).</summary>
public sealed record ApplicationSummary(
    Guid Id, Guid ListingId, string ListingTitle, Guid MemberId, string MemberFirstName, string Status, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt);

/// <summary>
/// One application (D46): its answers so far, and what the activity's rules say of the young
/// person, judged afresh while it's a draft and as they stood when it was sent after that.
/// </summary>
public sealed record FamilyApplication(
    Guid Id, Guid ListingId, string ListingTitle, Guid? OrganisationId, Guid MemberId, string MemberFirstName, string Status, Guid? FormId,
    IReadOnlyDictionary<string, JsonElement> Answers, EligibilityVerdict Eligibility, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? WithdrawnAt)
{
    public bool IsDraft => Status == "draft";

    public bool IsOffered => Status == "offered";

    /// <summary>The states a family may still withdraw from (the server's <c>ApplicationStatuses.Withdrawable</c>).</summary>
    public bool CanWithdraw => Status is "submitted" or "under_review" or "waitlisted" or "offered" or "accepted";
}

/// <summary>False when a rule that must be met isn't; null when one can't be judged yet; true otherwise.</summary>
public sealed record EligibilityVerdict(bool? IsEligible, IReadOnlyList<RuleOutcome> Rules);

public sealed record RuleOutcome(Guid? RuleId, string Kind, bool IsHard, bool? Met);

/// <summary>One of an activity's rules, as a family is shown it before applying.</summary>
public sealed record EligibilityRule(Guid Id, string Kind, RuleSettings Config, bool IsHard);

public sealed record RuleSettings(int? Min, int? Max, DateOnly? On, IReadOnlyList<Guid>? NeighbourhoodIds, Guid? BadgeClassId, Guid? PlaylistId);

/// <summary>The questions an application answers, in the family's language where the organisation wrote one.</summary>
public sealed record ApplicationForm(Guid FormId, int Version, string Hash, IReadOnlyList<FormQuestion> Questions);

/// <summary>
/// A question: <see cref="Kind"/> is <c>short_text</c>, <c>long_text</c>, <c>number</c>,
/// <c>date</c>, <c>yes_no</c>, <c>single_choice</c>, <c>multi_choice</c> or <c>consent</c>.
/// </summary>
public sealed record FormQuestion(
    string Key, string Kind, string Label, string? Help, bool Required, bool Sensitive, IReadOnlyList<FormChoice>? Choices, int? MaxLength, decimal? Min, decimal? Max);

public sealed record FormChoice(string Key, string Label);

/// <summary>A conversation the person is in (D53, D75), with how many messages they haven't read.</summary>
public sealed record ConversationSummary(
    Guid Id, Guid OrgId, string OrgName, string Kind, string? Subject, bool IncludesMinor, DateTimeOffset? LastMessageAt, int Unread, DateTimeOffset? ClosedAt)
{
    /// <summary>An announcement to an activity's families (D54): read, never answered.</summary>
    public bool IsAnnouncement => Kind == "org_broadcast";
}

/// <summary>
/// A conversation's messages. <see cref="ReadByOrganisation"/> is what the screen tells everyone:
/// with a young person in it, an admin of the organisation reads it too (T1).
/// </summary>
public sealed record Conversation(
    Guid Id, Guid OrgId, string OrgName, string Kind, string? Subject, bool ReadByOrganisation, IReadOnlyList<string> Roles, IReadOnlyList<ConversationMessage> Messages)
{
    public bool IsAnnouncement => Kind == "org_broadcast";
}

/// <summary>A message; a held one is shown only to its sender, until a moderator lets it through.</summary>
public sealed record ConversationMessage(Guid Id, bool FromMe, string SenderRole, string Body, string State, DateTimeOffset SentAt)
{
    public bool IsHeld => State == "held";
}

/// <summary>A waiver an application asks for (D47), signed against its exact text's <see cref="Hash"/>.</summary>
public sealed record ApplicationWaiver(Guid WaiverId, string Title, int Version, string BodyMarkdown, string Hash, bool IsRequired, DateTimeOffset? SignedAt);

/// <summary>What an application's activity charges (D55), with only the price levels this household fits.</summary>
public sealed record FeeQuote(Guid ApplicationId, short? HouseholdSize, IReadOnlyList<Fee> Fees);

public sealed record Fee(Guid Id, string Label, int AmountCents, string Currency, string Cadence, bool IsDefault, IReadOnlyList<FeeTier> Tiers)
{
    /// <summary>Charged once for the place; weekly, monthly and per-session fees are paid again, one charge each (D73).</summary>
    public bool IsOncePerPlace => Cadence is "one_time" or "seasonal";
}

public sealed record FeeTier(Guid Id, string Label, int AmountCents);

/// <summary>
/// A payment as the family sees it. The organisation's note on a waiver isn't read;
/// <see cref="HostedPaymentUrl"/> is a page of the provider's own (T20).
/// </summary>
public sealed record Payment(Guid Id, Guid? FeeScheduleId, int AmountCents, string Provider, string Status, DateTimeOffset CreatedAt, Uri? HostedPaymentUrl = null)
{
    public bool IsOpen => Status is "created" or "requires_action" or "processing";

    public bool IsSettled => Status is "succeeded" or "waived";
}

/// <summary>What a consent scope means, in the exact words a grant is recorded against (its <see cref="Hash"/>).</summary>
public sealed record Disclosure(string Scope, string Version, string Text, string Hash, IReadOnlyList<string> Methods, bool AvailableUnder13);

/// <summary>
/// A passkey ceremony (D38): <see cref="Options"/> as the platform's passkey API takes them
/// (WebAuthn JSON), and <see cref="State"/> to send back with its answer.
/// </summary>
public sealed record PasskeyCeremony(JsonElement Options, string State);

/// <summary>An external provider the region signs in with (D41): its key, for the web's start route, and its name.</summary>
public sealed record ExternalProvider(string Key, string Name);

public sealed record PasskeyInfo(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool IsBackedUp);

/// <summary>The terms of use: the exact words, and the hash agreeing to them names (D37).</summary>
public sealed record TermsDocument(string Kind, string Version, string Text, string Hash);

/// <summary>Where the signed-in person stands with the current terms.</summary>
public sealed record MyTerms(string Version, string Hash, DateTimeOffset? AcceptedAt);

/// <summary>An RFC 9457 problem from the API, or one the app makes for a request that never arrived.</summary>
public sealed record ApiProblem(string Type, string? Title, int Status, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public const string Prefix = "urn:brighttime:problem:";

    /// <summary>The app could not reach the server.</summary>
    public const string OfflineType = "urn:brighttime:app:offline";

    /// <summary>The server answered with something the app could not read.</summary>
    public const string UnreadableType = "urn:brighttime:app:unreadable";

    /// <summary>The error code after the prefix (<c>consent.token_invalid</c>), or null for another kind of problem.</summary>
    public string? Code => Type.StartsWith(Prefix, StringComparison.Ordinal) ? Type[Prefix.Length..] : null;

    public static ApiProblem Offline { get; } = new(OfflineType, null, 0);
}
