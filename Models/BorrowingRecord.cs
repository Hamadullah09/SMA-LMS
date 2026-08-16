using System.ComponentModel.DataAnnotations;

namespace Library_Management_system.Models
{
    public class BorrowingRecord
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(150)]
        public string Username { get; set; } = string.Empty;

        public int? ReservationId { get; set; }
        public CartItem? Reservation { get; set; }

        public int BookId { get; set; }
        public Book? Book { get; set; }

        public DateTime BorrowDate { get; set; }
        public DateTime DueDate { get; set; }
        public int DurationDays { get; set; } = 14;
        public DateTime? ReturnDate { get; set; }

        [MaxLength(450)]
        public string? ReturnUserId { get; set; }

        [MaxLength(30)]
        public string Status { get; set; } = "active";

        [MaxLength(30)]
        public string Source { get; set; } = "in_person";

        [MaxLength(150)]
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }

        public Fine? Fine { get; set; }

        // ---- SMA LMS Phase 3, stage 4 ----
        // The loan is evolved in place rather than replaced by a new table, so no borrowing
        // history is lost (specification section 87). Everything below is nullable until the
        // stage 5 migration has verified every row is mapped.

        /// <summary>
        /// The physical item borrowed. This is what makes RFID possible - BookId above points at
        /// the title, which cannot identify a tag.
        /// </summary>
        public int? BookCopyId { get; set; }
        public Domain.Entities.BookCopy? BookCopy { get; set; }

        /// <summary>Human-facing reference, e.g. SMA-LIB-2026-000001 (specification section 41).</summary>
        [MaxLength(60)]
        public string? TransactionNumber { get; set; }

        /// <summary>Replaces the Username string above with real referential integrity.</summary>
        public int? StudentId { get; set; }
        public Domain.Entities.Student? Student { get; set; }

        public Domain.Enums.CirculationMethod IssueMethod { get; set; }
            = Domain.Enums.CirculationMethod.Manual;

        public Domain.Enums.CirculationMethod? ReturnMethod { get; set; }

        /// <summary>Which reader performed the scan, when the operation was RFID-driven.</summary>
        public int? IssueReaderId { get; set; }
        public int? ReturnReaderId { get; set; }

        /// <summary>
        /// Optimistic concurrency token. The real guarantee against double-issue is the unique
        /// filtered index added in stage 5 (specification section 42).
        /// </summary>
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
