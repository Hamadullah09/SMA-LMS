using System.ComponentModel.DataAnnotations;
using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Domain.Entities;

/// <summary>
/// One physical item on a shelf. This is the entity RFID tags map to, and the entity a student
/// actually borrows (specification section 40).
///
/// The inherited schema conflated title and copy - Book carried Quantity as a scalar and
/// BorrowingRecord pointed at the title - which made it impossible to say which physical item a
/// tag denoted. See ARCHITECTURE.md section 3.1.
///
/// Stage 1 is additive: this references the existing Models.Book so the running catalogue keeps
/// working. Later stages backfill copies and repoint borrowing history
/// (DATABASE_ARCHITECTURE.md section 5).
/// </summary>
public class BookCopy
{
    public int Id { get; set; }

    /// <summary>The bibliographic title this item is a copy of.</summary>
    public int BookId { get; set; }
    public Models.Book? Book { get; set; }

    /// <summary>
    /// Human-readable identifier unique within the title, e.g. "001".
    /// Copies generated for pre-RFID borrowing history use "LEGACY" so reconstructed
    /// records are never presented as if they were precise.
    /// </summary>
    [Required, MaxLength(30)]
    public string CopyNumber { get; set; } = string.Empty;

    /// <summary>Library-wide barcode/accession number where one exists.</summary>
    [MaxLength(60)]
    public string? AccessionNumber { get; set; }

    public BookCopyStatus Status { get; set; } = BookCopyStatus.Available;

    public BookCondition Condition { get; set; } = BookCondition.Good;

    /// <summary>
    /// Most precise known location. Null is legitimate - specification section 9 requires
    /// showing the most precise location available, not inventing one.
    /// </summary>
    public int? ShelfPositionId { get; set; }
    public ShelfPosition? ShelfPosition { get; set; }

    /// <summary>Fallback when the copy is shelved only to shelf or section level.</summary>
    public int? ShelfId { get; set; }
    public Shelf? Shelf { get; set; }

    public int? LibrarySectionId { get; set; }
    public LibrarySection? LibrarySection { get; set; }

    public DateTime? AcquisitionDate { get; set; }

    [Range(0, double.MaxValue)]
    public decimal? AcquisitionCost { get; set; }

    [MaxLength(200)]
    public string? AcquisitionSource { get; set; }

    /// <summary>Set when the copy is marked lost, damaged or missing (specification section 30).</summary>
    [MaxLength(1000)]
    public string? StatusNote { get; set; }
    public DateTime? StatusChangedUtc { get; set; }
    [MaxLength(150)]
    public string? StatusChangedBy { get; set; }

    public DateTime? LastSeenUtc { get; set; }
    public int? LastSeenReaderId { get; set; }

    [MaxLength(150)]
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token. Prevents two librarians issuing the same physical copy
    /// simultaneously (specification section 42). Backed by a unique filtered index on the
    /// borrowing table as the real guarantee.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<BookRfidTag> RfidTags { get; set; } = new List<BookRfidTag>();

    /// <summary>A copy is borrowable only when on the shelf and in usable condition.</summary>
    public bool IsBorrowable =>
        Status == BookCopyStatus.Available && Condition != BookCondition.Unusable;
}
