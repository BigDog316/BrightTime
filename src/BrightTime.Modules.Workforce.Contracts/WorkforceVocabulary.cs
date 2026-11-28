namespace BrightTime.Modules.Workforce.Contracts;

/// <summary>A mentor profile's states (<c>ck_mentor_status</c>), as D129 uses them.</summary>
public static class MentorStatuses
{
    /// <summary>Written, and not listed: only its owner sees it.</summary>
    public const string Draft = "draft";

    /// <summary>Listed: organisations' staff find it in search, while its owner's clearances are current.</summary>
    public const string Active = "active";

    /// <summary>Listed before, and taken off the list by its owner.</summary>
    public const string Paused = "paused";
}

/// <summary>The taxonomy vocabularies a mentor's profile draws on.</summary>
public static class MentorVocabularies
{
    public const string Skill = "skill";
    public const string Language = "language";
}
