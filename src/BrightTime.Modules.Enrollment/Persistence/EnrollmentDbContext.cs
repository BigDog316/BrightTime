using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Persistence;

// The family profile's tables (db/schema/50-enrollment.sql). The *_enc columns hold AES-256-GCM
// ciphertext under the tenant's data key (D32); this module encrypts and decrypts them, and
// nothing else reads them.

public sealed class FamilyProfileRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OwnerUserId { get; init; }

    public short? HouseholdSize { get; set; }

    public string? PrimaryLanguage { get; set; }

    public bool NeedsInterpreter { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class FamilyMemberRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyProfileId { get; init; }

    public Guid? UserId { get; set; }

    public required string MemberRole { get; set; }

    public required byte[] FirstNameEnc { get; set; }

    public required byte[] LastNameEnc { get; set; }

    public string? PreferredName { get; set; }

    public byte[]? DateOfBirthEnc { get; set; }

    public DateOnly? BirthMonth { get; set; }

    public short? Grade { get; set; }

    public Guid? SchoolId { get; set; }

    public Guid? NeighbourhoodId { get; set; }

    public string? Pronouns { get; set; }

    public string? RelationshipToOwner { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SupportNeedRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyMemberId { get; init; }

    public required string NeedKind { get; init; }

    public byte[]? DetailEnc { get; init; }

    public bool ShareWithProviders { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class FamilyContactRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyProfileId { get; init; }

    public required byte[] NameEnc { get; set; }

    public required string Relationship { get; set; }

    public required byte[] PhoneEnc { get; set; }

    public byte[]? PhoneBlindIndex { get; set; }

    public byte[]? EmailEnc { get; set; }

    public bool IsEmergency { get; set; }

    public bool CanPickUp { get; set; }

    public short SortOrder { get; set; }
}

/// <summary>One organisation's view of a family profile: which members, which fields, until when (D44).</summary>
public sealed class ProfileShareRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid FamilyProfileId { get; init; }

    public Guid OrgId { get; init; }

    public Guid? ListingId { get; init; }

    public required Guid[] MemberIds { get; init; }

    public required string[] SharedFields { get; init; }

    public Guid GrantedByUserId { get; init; }

    public DateTimeOffset GrantedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A provider's registration form, one version of it (D45). Published versions never change.</summary>
public sealed class FormDefinitionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? OrgId { get; init; }

    public required string Name { get; set; }

    public int Version { get; init; }

    public required string Status { get; set; }

    public required string SchemaJson { get; set; }

    public required byte[] SchemaHash { get; set; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>The form a listing's registration asks families to fill in.</summary>
public sealed class ListingFormRecord
{
    public Guid ListingId { get; init; }

    public Guid FormDefinitionId { get; set; }
}

/// <summary>One eligibility rule on a listing (D46): a kind and its typed config, hard or advisory.</summary>
public sealed class EligibilityRuleRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public required string RuleKind { get; init; }

    public required string Config { get; init; }

    public bool IsHard { get; init; }

    public string? MessageKey { get; init; }

    public short SortOrder { get; init; }
}

/// <summary>A family's application for one member to one listing (D46).</summary>
public sealed class ApplicationRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public Guid? SeatPoolId { get; set; }

    public Guid FamilyProfileId { get; init; }

    public Guid FamilyMemberId { get; init; }

    public Guid? SubmittedByUserId { get; set; }

    public Guid? FormDefinitionId { get; init; }

    public required string Status { get; set; }

    /// <summary>The answers as given. Sensitive ones are sealed under the tenant's data key.</summary>
    public required string AnswersSnapshot { get; set; }

    public required string EligibilityResult { get; set; }

    public bool? IsEligible { get; set; }

    public decimal? PriorityScore { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    public string? DecisionNote { get; set; }

    public DateTimeOffset? WithdrawnAt { get; set; }

    public string? IdempotencyKey { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One answer, normalised for reporting. A sensitive answer's row records only that it was answered.</summary>
public sealed class ApplicationAnswerRecord
{
    public Guid ApplicationId { get; init; }

    public required string QuestionKey { get; init; }

    public string? ValueText { get; init; }

    public decimal? ValueNumber { get; init; }

    public DateOnly? ValueDate { get; init; }

    public bool? ValueBool { get; init; }

    public string[]? ValueChoices { get; init; }

    public bool IsSensitive { get; init; }
}

/// <summary>A provider's waiver, one version of it (D47). A published version's text never changes.</summary>
public sealed class WaiverRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? OrgId { get; init; }

    public required string Title { get; init; }

    public required string BodyMd { get; set; }

    public required byte[] BodyHash { get; set; }

    public int Version { get; init; }

    public bool RequiresGuardian { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A waiver a listing asks families to sign, required or optional.</summary>
public sealed class ListingWaiverRecord
{
    public Guid ListingId { get; init; }

    public Guid WaiverId { get; init; }

    public bool IsRequired { get; set; }
}

/// <summary>One signature: who signed which text, for whom, when. Append-only evidence.</summary>
public sealed class WaiverSignatureRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid WaiverId { get; init; }

    public Guid? EnrollmentId { get; init; }

    public Guid? ApplicationId { get; init; }

    public Guid FamilyMemberId { get; init; }

    public Guid SignedByUserId { get; init; }

    public required string SignerRole { get; init; }

    public required string TypedName { get; init; }

    public Guid? SignatureMediaId { get; init; }

    public required byte[] BodyHashSigned { get; init; }

    public DateTimeOffset SignedAt { get; init; }

    public byte[]? IpHash { get; init; }

    public byte[]? UserAgentHash { get; init; }
}

/// <summary>
/// A listing's seats and waitlist (D48): a counter row taken under <c>FOR UPDATE</c>, so the last
/// seat and waitlist order are right without SERIALIZABLE.
/// </summary>
public sealed class SeatPoolRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public required string Label { get; init; }

    public int Capacity { get; set; }

    public int SeatsTaken { get; set; }

    public int? WaitlistCapacity { get; set; }

    public int WaitlistTaken { get; set; }

    public int NextWaitlistPosition { get; set; }
}

/// <summary>A young person's place in a listing: enrolled, or on its waitlist.</summary>
public sealed class EnrollmentRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public Guid? SeatPoolId { get; init; }

    public Guid? ApplicationId { get; init; }

    public Guid FamilyMemberId { get; init; }

    public Guid? YouthUserId { get; set; }

    public Guid OrgId { get; init; }

    public required string SeatKind { get; set; }

    public int? WaitlistPosition { get; set; }

    public required string Status { get; set; }

    public DateOnly? StartsOn { get; init; }

    public DateOnly? EndsOn { get; init; }

    public DateTimeOffset EnrolledAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? EndReason { get; set; }
}

/// <summary>What a listing charges (D55): an amount and how often, one of them the default.</summary>
public sealed class FeeScheduleRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    public required string Label { get; set; }

    public int AmountCents { get; set; }

    public required string Currency { get; init; }

    public required string Cadence { get; set; }

    public bool IsDefault { get; set; }
}

/// <summary>A reduced amount for households that fit it, by size and by the income band the family picks.</summary>
public sealed class SlidingScaleTierRecord
{
    public Guid Id { get; init; }

    public Guid FeeScheduleId { get; init; }

    public required string Label { get; init; }

    public short? HouseholdSizeMin { get; init; }

    public short? HouseholdSizeMax { get; init; }

    public string? IncomeBand { get; init; }

    public int AmountCents { get; init; }

    public short SortOrder { get; init; }
}

/// <summary>A payment in flight with the provider: amount, status and the provider's reference. Never a card (T20).</summary>
public sealed class PaymentIntentRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid? EnrollmentId { get; init; }

    public Guid? ApplicationId { get; init; }

    public Guid? FeeScheduleId { get; init; }

    public int AmountCents { get; init; }

    public required string Currency { get; init; }

    public required string Provider { get; init; }

    public string? ProviderRef { get; init; }

    public required string Status { get; set; }

    public string? WaivedReason { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The Enrollment module's context (M3a/M3b): the family profile, shares, forms, eligibility, applications, waivers, seats, enrolments and fees.</summary>
public sealed class EnrollmentDbContext(DbContextOptions<EnrollmentDbContext> options, ITenantContext tenantContext)
    : BrightTimeDbContext(options, tenantContext)
{
    public const string SchemaName = "enrollment";
    public const string MigrationsHistoryTable = "__ef_migrations_enrollment";

    public override string Schema => SchemaName;

    public DbSet<FamilyProfileRecord> FamilyProfiles => Set<FamilyProfileRecord>();

    public DbSet<FamilyMemberRecord> FamilyMembers => Set<FamilyMemberRecord>();

    public DbSet<SupportNeedRecord> SupportNeeds => Set<SupportNeedRecord>();

    public DbSet<FamilyContactRecord> FamilyContacts => Set<FamilyContactRecord>();

    public DbSet<ProfileShareRecord> ProfileShares => Set<ProfileShareRecord>();

    public DbSet<FormDefinitionRecord> FormDefinitions => Set<FormDefinitionRecord>();

    public DbSet<ListingFormRecord> ListingForms => Set<ListingFormRecord>();

    public DbSet<EligibilityRuleRecord> EligibilityRules => Set<EligibilityRuleRecord>();

    public DbSet<ApplicationRecord> Applications => Set<ApplicationRecord>();

    public DbSet<ApplicationAnswerRecord> ApplicationAnswers => Set<ApplicationAnswerRecord>();

    public DbSet<WaiverRecord> Waivers => Set<WaiverRecord>();

    public DbSet<ListingWaiverRecord> ListingWaivers => Set<ListingWaiverRecord>();

    public DbSet<WaiverSignatureRecord> WaiverSignatures => Set<WaiverSignatureRecord>();

    public DbSet<SeatPoolRecord> SeatPools => Set<SeatPoolRecord>();

    public DbSet<EnrollmentRecord> Enrollments => Set<EnrollmentRecord>();

    public DbSet<FeeScheduleRecord> FeeSchedules => Set<FeeScheduleRecord>();

    public DbSet<SlidingScaleTierRecord> SlidingScaleTiers => Set<SlidingScaleTierRecord>();

    public DbSet<PaymentIntentRecord> PaymentIntents => Set<PaymentIntentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FamilyProfileRecord>(b =>
        {
            b.ToTable("family_profile");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.OwnerUserId).IsUnique();
        });

        modelBuilder.Entity<FamilyMemberRecord>(b =>
        {
            b.ToTable("family_member");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyProfileId);
            b.HasIndex(x => x.UserId).HasFilter("user_id is not null");
        });

        modelBuilder.Entity<SupportNeedRecord>(b =>
        {
            b.ToTable("family_member_support_need");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyMemberId);
        });

        modelBuilder.Entity<FamilyContactRecord>(b =>
        {
            b.ToTable("family_contact");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyProfileId);
        });

        modelBuilder.Entity<ProfileShareRecord>(b =>
        {
            b.ToTable("profile_share");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.OrgId, x.GrantedAt }).IsDescending(false, true).HasFilter("revoked_at is null");
        });

        modelBuilder.Entity<FormDefinitionRecord>(b =>
        {
            b.ToTable("form_definition");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SchemaJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.TenantId, x.OrgId, x.Name, x.Version }).IsUnique();
        });

        modelBuilder.Entity<ListingFormRecord>(b =>
        {
            b.ToTable("listing_form");
            b.HasKey(x => x.ListingId);
            b.Property(x => x.ListingId).ValueGeneratedNever();
        });

        modelBuilder.Entity<EligibilityRuleRecord>(b =>
        {
            b.ToTable("eligibility_rule");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Config).HasColumnType("jsonb");
            b.HasIndex(x => new { x.ListingId, x.SortOrder });
        });

        modelBuilder.Entity<ApplicationRecord>(b =>
        {
            b.ToTable("application");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.AnswersSnapshot).HasColumnType("jsonb");
            b.Property(x => x.EligibilityResult).HasColumnType("jsonb");
            b.Property(x => x.PriorityScore).HasPrecision(8, 3);
            b.HasIndex(x => new { x.ListingId, x.FamilyMemberId }).IsUnique()
                .HasFilter("status not in ('withdrawn','expired','declined','rejected')");
            b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key is not null");
            b.HasIndex(x => new { x.ListingId, x.Status, x.SubmittedAt });
            b.HasIndex(x => new { x.FamilyMemberId, x.SubmittedAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<ApplicationAnswerRecord>(b =>
        {
            b.ToTable("application_answer");
            b.HasKey(x => new { x.ApplicationId, x.QuestionKey });
            b.HasIndex(x => x.QuestionKey).HasFilter("not is_sensitive");
        });

        modelBuilder.Entity<WaiverRecord>(b =>
        {
            b.ToTable("waiver");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.TenantId, x.OrgId, x.Title, x.Version }).IsUnique();
        });

        modelBuilder.Entity<ListingWaiverRecord>(b =>
        {
            b.ToTable("listing_waiver");
            b.HasKey(x => new { x.ListingId, x.WaiverId });
        });

        modelBuilder.Entity<WaiverSignatureRecord>(b =>
        {
            b.ToTable("waiver_signature");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.FamilyMemberId);
            b.HasIndex(x => new { x.WaiverId, x.FamilyMemberId, x.BodyHashSigned }).IsUnique();
        });

        modelBuilder.Entity<SeatPoolRecord>(b =>
        {
            b.ToTable("seat_pool");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.ListingId, x.Label }).IsUnique();
        });

        modelBuilder.Entity<EnrollmentRecord>(b =>
        {
            b.ToTable("enrollment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.ListingId, x.FamilyMemberId }).IsUnique().HasFilter("status = 'active'");
            b.HasIndex(x => new { x.SeatPoolId, x.WaitlistPosition }).IsUnique().HasFilter("seat_kind = 'waitlist' and status = 'active'");
            b.HasIndex(x => new { x.OrgId, x.ListingId }).HasFilter("status = 'active'");
            b.HasIndex(x => x.YouthUserId).HasFilter("youth_user_id is not null");
        });

        modelBuilder.Entity<FeeScheduleRecord>(b =>
        {
            b.ToTable("fee_schedule");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Currency).HasColumnType("character(3)");
            b.HasIndex(x => x.ListingId).IsUnique().HasFilter("is_default");
        });

        modelBuilder.Entity<SlidingScaleTierRecord>(b =>
        {
            b.ToTable("sliding_scale_tier");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<PaymentIntentRecord>(b =>
        {
            b.ToTable("payment_intent");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Currency).HasColumnType("character(3)");
            b.HasIndex(x => new { x.Provider, x.ProviderRef }).IsUnique();
            b.HasIndex(x => x.EnrollmentId);
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
