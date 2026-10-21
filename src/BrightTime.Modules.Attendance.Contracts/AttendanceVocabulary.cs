namespace BrightTime.Modules.Attendance.Contracts;

/// <summary>An attendance record's states (<c>ck_attendance_state</c>).</summary>
public static class AttendanceStates
{
    public const string Present = "present";
    public const string Absent = "absent";
    public const string Late = "late";
    public const string Excused = "excused";
    public const string LeftEarly = "left_early";
    public const string Unknown = "unknown";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>([Present, Absent, Late, Excused, LeftEarly, Unknown], StringComparer.Ordinal);
}

/// <summary>How an attendance record was taken (<c>ck_attendance_method</c>).</summary>
public static class AttendanceMethods
{
    public const string Manual = "manual";
    public const string Qr = "qr";
    public const string Kiosk = "kiosk";
    public const string SelfCheckIn = "self_check_in";
    public const string Import = "import";
    public const string OfflineSync = "offline_sync";
}

/// <summary>What became of one item of an offline batch.</summary>
public static class SyncStatuses
{
    /// <summary>Written now.</summary>
    public const string Applied = "applied";

    /// <summary>Already written by an earlier replay of the same item: a success, not an error.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Someone else recorded this young person for this meeting first; theirs stands.</summary>
    public const string Conflict = "conflict";

    /// <summary>Not valid, for the reason given.</summary>
    public const string Rejected = "rejected";
}
