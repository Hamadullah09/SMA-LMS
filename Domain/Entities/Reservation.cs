using System.ComponentModel.DataAnnotations;
using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Domain.Entities;

/// <summary>
/// A hold on a TITLE, not on a copy (specification section 26).
///
/// Reserving a specific copy would be wrong: the student wants the book, and whichever copy comes
/// back first should satisfy them. The copy is bound only at the moment the hold is fulfilled.
///
/// Separate from the inherited CartItem, which the Phase 1 audit found doing double duty as both
/// shopping cart and reservation. A cart is an intention; a reservation is a queue position with
/// an expiry and a notification.
/// </summary>
public class Reservation
{
    public int Id { get; set; }

    public int StudentId { get; set; }
    public Student? Student { get; set; }

    /// <summary>The title being waited for.</summary>
    public int BookId { get; set; }
    public Models.Book? Book { get; set; }

    public ReservationStatus Status { get; set; } = ReservationStatus.Queued;

    /// <summary>
    /// FIFO position within the title's queue (section 26). Recomputed when a hold is
    /// cancelled or expires so positions stay contiguous.
    /// </summary>
    public int QueuePosition { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Set when a copy is set aside and the student notified.</summary>
    public DateTime? AvailableSinceUtc { get; set; }

    /// <summary>
    /// When the hold lapses if uncollected. Driven by Reservations.ExpiryDays so the shelf is
    /// not blocked indefinitely.
    /// </summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>The specific copy set aside, bound only at fulfilment time.</summary>
    public int? ReservedCopyId { get; set; }
    public BookCopy? ReservedCopy { get; set; }

    public DateTime? FulfilledUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }

    [MaxLength(300)]
    public string? CancellationReason { get; set; }

    public bool IsOpen => Status is ReservationStatus.Queued or ReservationStatus.Available;
}
