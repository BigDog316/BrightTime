namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>What a listing is. Each kind carries different required fields, enforced by the schema and by <c>ListingValidator</c>.</summary>
public enum ListingKind
{
    Program,
    Event,
    Job,
    Resource,
}

public enum Modality
{
    InPerson,
    Virtual,
    Hybrid,
}

public enum CostModel
{
    Free,
    Fixed,
    SlidingScale,
    Varies,
    StipendPaid,
}

public enum RegistrationMode
{
    /// <summary>Drop-in or informational; nothing to register for.</summary>
    None,

    /// <summary>Hand-off to the provider's own registration page.</summary>
    External,

    /// <summary>Built-in registration with capacity and waitlist.</summary>
    Builtin,

    WaitlistOnly,
}

public enum ListingStatus
{
    Draft,
    PendingReview,
    ChangesRequested,
    Published,
    Unpublished,
    Rejected,
    Archived,
}

/// <summary>Database representations, matching the CHECK constraints in <c>db/schema/40-opportunity.sql</c>.</summary>
public static class ListingEnumExtensions
{
    public static string ToDbValue(this ListingKind kind) => kind switch
    {
        ListingKind.Program => "program",
        ListingKind.Event => "event",
        ListingKind.Job => "job",
        ListingKind.Resource => "resource",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped listing kind."),
    };

    public static string ToDbValue(this Modality modality) => modality switch
    {
        Modality.InPerson => "in_person",
        Modality.Virtual => "virtual",
        Modality.Hybrid => "hybrid",
        _ => throw new ArgumentOutOfRangeException(nameof(modality), modality, "Unmapped modality."),
    };

    public static string ToDbValue(this CostModel model) => model switch
    {
        CostModel.Free => "free",
        CostModel.Fixed => "fixed",
        CostModel.SlidingScale => "sliding_scale",
        CostModel.Varies => "varies",
        CostModel.StipendPaid => "stipend_paid",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unmapped cost model."),
    };

    public static string ToDbValue(this RegistrationMode mode) => mode switch
    {
        RegistrationMode.None => "none",
        RegistrationMode.External => "external",
        RegistrationMode.Builtin => "builtin",
        RegistrationMode.WaitlistOnly => "waitlist_only",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped registration mode."),
    };

    public static string ToDbValue(this ListingStatus status) => status switch
    {
        ListingStatus.Draft => "draft",
        ListingStatus.PendingReview => "pending_review",
        ListingStatus.ChangesRequested => "changes_requested",
        ListingStatus.Published => "published",
        ListingStatus.Unpublished => "unpublished",
        ListingStatus.Rejected => "rejected",
        ListingStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped listing status."),
    };
}
