using System.ComponentModel.DataAnnotations;
using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Domain.Entities;

// RFID entities. See RFID_ARCHITECTURE.md.
//
// Tags are modelled as assignment RECORDS, not string columns, so replacement never destroys
// history (specification sections 6, 36, 87). Rssi and Antenna are nullable because it is not
// known whether the actual reader reports them - specification section 4E says to persist only
// fields the hardware genuinely provides.

/// <summary>Fields shared by student-card and book-copy tag assignments.</summary>
public abstract class RfidTagAssignment
{
    public int Id { get; set; }

    /// <summary>EPC - the primary identifier for an EPC Gen2 / ISO 18000-6C tag.</summary>
    [Required, MaxLength(128)]
    public string Epc { get; set; } = string.Empty;

    /// <summary>Factory-unique TID where the tag exposes one. Not guaranteed.</summary>
    [MaxLength(128)]
    public string? Tid { get; set; }

    public RfidTagState State { get; set; } = RfidTagState.Active;

    /// <summary>
    /// Denormalised from State so a unique filtered index can enforce
    /// "one live assignment per EPC" at the database level.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime AssignedUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(150)]
    public string? AssignedBy { get; set; }

    public DateTime? EndedUtc { get; set; }

    [MaxLength(150)]
    public string? EndedBy { get; set; }

    [MaxLength(400)]
    public string? EndedReason { get; set; }

    public DateTime? LastScannedUtc { get; set; }
    public int? LastScannedReaderId { get; set; }

    /// <summary>Ends this assignment without deleting it (specification section 87).</summary>
    public void End(RfidTagState newState, string? by, string? reason, DateTime utcNow)
    {
        State = newState;
        IsActive = false;
        EndedUtc = utcNow;
        EndedBy = by;
        EndedReason = reason;
    }
}

public class StudentRfidTag : RfidTagAssignment
{
    public int StudentId { get; set; }
    public Student? Student { get; set; }
}

public class BookRfidTag : RfidTagAssignment
{
    public int BookCopyId { get; set; }
    public BookCopy? BookCopy { get; set; }
}

/// <summary>A physical reader (specification sections 5, 4C).</summary>
public class RfidReader
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Vendor model string, e.g. "D2184". Free text - the model is not yet verified.</summary>
    [MaxLength(100)]
    public string? Model { get; set; }

    [MaxLength(100)]
    public string? SerialNumber { get; set; }

    [MaxLength(50)]
    public string? FirmwareVersion { get; set; }

    public RfidTransport Transport { get; set; } = RfidTransport.Simulator;

    // Network transport
    [MaxLength(100)]
    public string? Host { get; set; }
    public int? Port { get; set; }

    // Serial transport
    [MaxLength(20)]
    public string? ComPort { get; set; }
    public int? BaudRate { get; set; }

    public RfidReaderPurpose Purpose { get; set; } = RfidReaderPurpose.CirculationDesk;

    /// <summary>Where the reader physically sits, for correlating scans to a location.</summary>
    public int? LibrarySectionId { get; set; }
    public LibrarySection? LibrarySection { get; set; }

    [MaxLength(200)]
    public string? LocationDescription { get; set; }

    /// <summary>Number of antennas configured, where the reader exposes more than one.</summary>
    public int? AntennaCount { get; set; }

    public bool IsEnabled { get; set; } = true;

    public RfidReaderStatus Status { get; set; } = RfidReaderStatus.Offline;

    // Health (specification section 4H)
    public DateTime? LastHeartbeatUtc { get; set; }
    public DateTime? LastSuccessfulCommunicationUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ReconnectAttempts { get; set; }
    public int? LastLatencyMs { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<RfidScanEvent> ScanEvents { get; set; } = new List<RfidScanEvent>();
}

/// <summary>
/// One logical observation of a tag, after deduplication. A reader seeing the same EPC fifty
/// times in two seconds produces ONE row with ReadCount incremented - specification section 4D
/// forbids a database transaction per RF observation.
/// </summary>
public class RfidScanEvent
{
    public long Id { get; set; }

    public int ReaderId { get; set; }
    public RfidReader? Reader { get; set; }

    [Required, MaxLength(128)]
    public string Epc { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? Tid { get; set; }

    /// <summary>Signal strength, if the reader reports it. Unknown for the D2184.</summary>
    public int? Rssi { get; set; }

    /// <summary>Antenna number, if the reader reports it. Unknown for the D2184.</summary>
    public int? Antenna { get; set; }

    public DateTime FirstObservedUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }

    /// <summary>How many raw observations collapsed into this event.</summary>
    public int ReadCount { get; set; } = 1;

    /// <summary>What the EPC resolved to, or null when the tag is unknown.</summary>
    public RfidTagKind? ResolvedKind { get; set; }
    public int? ResolvedStudentId { get; set; }
    public int? ResolvedBookCopyId { get; set; }

    /// <summary>
    /// Ties scan -> validation -> transaction -> notification -> audit into one traceable
    /// operation (specification sections 4I, 83).
    /// </summary>
    [MaxLength(64)]
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Links a scan to the circulation outcome it produced, successful or not
/// (specification section 4I).
/// </summary>
public class RfidTransaction
{
    public long Id { get; set; }

    public long ScanEventId { get; set; }
    public RfidScanEvent? ScanEvent { get; set; }

    public RfidOperation Operation { get; set; }

    public int? StudentId { get; set; }
    public int? BookCopyId { get; set; }
    public int ReaderId { get; set; }

    /// <summary>Set only when the operation produced a loan.</summary>
    public int? BorrowingTransactionId { get; set; }

    public bool Succeeded { get; set; }

    [MaxLength(600)]
    public string? FailureReason { get; set; }

    /// <summary>Librarian on duty, where the operation was desk-assisted.</summary>
    [MaxLength(450)]
    public string? OperatorUserId { get; set; }

    [MaxLength(64)]
    public string? CorrelationId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
