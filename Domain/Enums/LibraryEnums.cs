namespace Library_Management_system.Domain.Enums;

/// <summary>Status of a single physical item on the shelf (specification section 8).</summary>
public enum BookCopyStatus
{
    Available = 0,
    Issued = 1,
    Reserved = 2,
    Lost = 3,
    Damaged = 4,
    UnderMaintenance = 5,
    Missing = 6,
    Archived = 7,
    InTransit = 8
}

/// <summary>Physical condition, tracked separately from availability.</summary>
public enum BookCondition
{
    New = 0,
    Good = 1,
    Fair = 2,
    Poor = 3,
    Unusable = 4
}

public enum StudentStatus
{
    Active = 0,
    Inactive = 1,
    Suspended = 2,
    Graduated = 3,
    Withdrawn = 4
}

/// <summary>How a circulation event was performed (specification section 41).</summary>
public enum CirculationMethod
{
    Rfid = 0,
    Manual = 1,
    System = 2
}

public enum BorrowingStatus
{
    Active = 0,
    Returned = 1,
    Overdue = 2,
    Lost = 3,
    Cancelled = 4
}

public enum ReservationStatus
{
    Queued = 0,
    Available = 1,
    Fulfilled = 2,
    Expired = 3,
    Cancelled = 4
}

/// <summary>Specification section 23.</summary>
public enum FineStatus
{
    Pending = 0,
    PartiallyPaid = 1,
    Paid = 2,
    Waived = 3,
    Cancelled = 4
}

/// <summary>Specification section 4C.</summary>
public enum RfidReaderStatus
{
    Offline = 0,
    Connecting = 1,
    Online = 2,
    Error = 3,
    Disabled = 4
}

/// <summary>What a reader is installed to do (specification section 5).</summary>
public enum RfidReaderPurpose
{
    Checkout = 0,
    Return = 1,
    StudentIdentification = 2,
    SecurityGate = 3,
    Inventory = 4,
    Tagging = 5,
    CirculationDesk = 6
}

/// <summary>
/// Physical transport to the reader. Which of these the D2184 supports is unknown -
/// see RFID_ARCHITECTURE.md section 1.
/// </summary>
public enum RfidTransport
{
    Simulator = 0,
    Tcp = 1,
    Serial = 2,
    Usb = 3,
    LocalAgent = 4
}

public enum RfidTagKind
{
    StudentCard = 0,
    BookCopy = 1
}

/// <summary>Why a tag assignment ended. History is never deleted (specification section 87).</summary>
public enum RfidTagState
{
    Active = 0,
    Replaced = 1,
    Revoked = 2,
    Lost = 3,
    Damaged = 4
}

public enum RfidOperation
{
    Identify = 0,
    Issue = 1,
    Return = 2,
    Inventory = 3,
    GateCheck = 4,
    Assignment = 5
}

public enum NotificationChannel
{
    Email = 0,

    /// <summary>
    /// No longer produced — the Telegram bot was removed. Kept so the numbering stays stable and
    /// any historical row still deserialises; delete it only alongside a data migration.
    /// </summary>
    Telegram = 1,

    InApp = 2
}

/// <summary>Outbox state (specification section 53).</summary>
public enum NotificationStatus
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    Failed = 3,
    Abandoned = 4,
    Cancelled = 5
}

public enum NotificationKind
{
    IssueConfirmation = 0,
    DueReminder = 1,
    DueToday = 2,
    OverdueWarning = 3,
    ReturnConfirmation = 4,
    FineNotice = 5,
    ReservationAvailable = 6,
    ReservationExpiring = 7,
    AccountRestricted = 8
}

public enum SecurityEventKind
{
    UnknownTag = 0,
    DuplicateTag = 1,
    UnauthorisedCheckout = 2,
    GateExitWithoutIssue = 3,
    BlockedStudentAttempt = 4,
    TagEntityMismatch = 5,
    ReaderError = 6,
    RepeatedFailedScan = 7
}

public enum SecurityEventSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Value type of a policy setting, so the engine can parse it safely.</summary>
public enum PolicyValueKind
{
    Integer = 0,
    Decimal = 1,
    Boolean = 2,
    String = 3
}
