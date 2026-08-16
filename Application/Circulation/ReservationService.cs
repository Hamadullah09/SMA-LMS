using Library_Management_system.Application.Notifications;
using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Circulation;

public sealed record ReservationResult(bool Succeeded, string Message, int? QueuePosition = null)
{
    public static ReservationResult Ok(string message, int? position = null) => new(true, message, position);
    public static ReservationResult Fail(string message) => new(false, message);
}

/// <summary>
/// Reservations (specification section 26): reserve an unavailable title, FIFO queue, cancellation,
/// expiry, and notification when a copy comes back.
/// </summary>
public interface IReservationService
{
    Task<ReservationResult> ReserveAsync(int studentId, int bookId, CancellationToken ct = default);
    Task<ReservationResult> CancelAsync(int reservationId, int studentId, CancellationToken ct = default);

    /// <summary>Called after a return: offers the copy to the next student in the queue.</summary>
    Task<Reservation?> FulfilNextAsync(int bookId, int bookCopyId, CancellationToken ct = default);

    /// <summary>Lapses uncollected holds and passes them down the queue.</summary>
    Task<int> ExpireStaleAsync(CancellationToken ct = default);
}

public sealed class ReservationService : IReservationService
{
    private readonly ApplicationDbContext _db;
    private readonly ILibraryPolicyService _policies;
    private readonly INotificationOutbox _outbox;
    private readonly ILogger<ReservationService> _logger;

    public ReservationService(
        ApplicationDbContext db,
        ILibraryPolicyService policies,
        INotificationOutbox outbox,
        ILogger<ReservationService> logger)
    {
        _db = db;
        _policies = policies;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<ReservationResult> ReserveAsync(int studentId, int bookId, CancellationToken ct = default)
    {
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null)
        {
            return ReservationResult.Fail("Your student record could not be found.");
        }

        if (student.Status != StudentStatus.Active || student.IsBorrowingBlocked)
        {
            return ReservationResult.Fail("Your account cannot place reservations at the moment.");
        }

        var book = await _db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
        {
            return ReservationResult.Fail("That book is not in the catalogue.");
        }

        // Reserving something already on the shelf wastes a queue slot - send them to fetch it.
        var availableNow = await _db.BookCopies
            .AsNoTracking()
            .CountAsync(c => c.BookId == bookId
                             && c.CopyNumber != "LEGACY"
                             && c.Status == BookCopyStatus.Available, ct);

        if (availableNow > 0)
        {
            return ReservationResult.Fail(
                "This book is on the shelf right now — you can borrow it at the desk without reserving.");
        }

        if (await _db.Reservations.AnyAsync(r =>
                r.StudentId == studentId && r.BookId == bookId
                && (r.Status == ReservationStatus.Queued || r.Status == ReservationStatus.Available), ct))
        {
            return ReservationResult.Fail("You already have a reservation for this book.");
        }

        var maxReservations = await _policies.GetIntAsync(LibraryPolicy.Keys.MaximumReservations, 3, ct);
        var openCount = await _db.Reservations.CountAsync(r =>
            r.StudentId == studentId
            && (r.Status == ReservationStatus.Queued || r.Status == ReservationStatus.Available), ct);

        if (openCount >= maxReservations)
        {
            return ReservationResult.Fail(
                $"You already have {openCount} reservations; the limit is {maxReservations}.");
        }

        // Position is one past the current tail of this title's queue.
        var tail = await _db.Reservations
            .Where(r => r.BookId == bookId && r.Status == ReservationStatus.Queued)
            .MaxAsync(r => (int?)r.QueuePosition, ct) ?? 0;

        var reservation = new Reservation
        {
            StudentId = studentId,
            BookId = bookId,
            Status = ReservationStatus.Queued,
            QueuePosition = tail + 1,
            CreatedUtc = DateTime.UtcNow
        };

        _db.Reservations.Add(reservation);
        await _db.SaveChangesAsync(ct);

        return ReservationResult.Ok(
            reservation.QueuePosition == 1
                ? "Reserved. You are next in line — we will email you when a copy is returned."
                : $"Reserved. You are number {reservation.QueuePosition} in the queue.",
            reservation.QueuePosition);
    }

    public async Task<ReservationResult> CancelAsync(int reservationId, int studentId, CancellationToken ct = default)
    {
        // Scoped by student: one student must not be able to cancel another's hold (§43).
        var reservation = await _db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId && r.StudentId == studentId, ct);

        if (reservation is null)
        {
            return ReservationResult.Fail("That reservation could not be found.");
        }

        if (!reservation.IsOpen)
        {
            return ReservationResult.Fail("That reservation is already closed.");
        }

        reservation.Status = ReservationStatus.Cancelled;
        reservation.CancelledUtc = DateTime.UtcNow;
        reservation.CancellationReason = "Cancelled by the student";

        // Persist the cancellation BEFORE resequencing. The resequence query filters on
        // Status == Queued in the store, so an unsaved cancellation is still seen as queued and
        // would be handed a position — leaving a gap for everyone behind it.
        await _db.SaveChangesAsync(ct);

        await ResequenceAsync(reservation.BookId, ct);
        await _db.SaveChangesAsync(ct);

        return ReservationResult.Ok("Reservation cancelled.");
    }

    public async Task<Reservation?> FulfilNextAsync(int bookId, int bookCopyId, CancellationToken ct = default)
    {
        var next = await _db.Reservations
            .Include(r => r.Student)
            .Where(r => r.BookId == bookId && r.Status == ReservationStatus.Queued)
            .OrderBy(r => r.QueuePosition)
            .FirstOrDefaultAsync(ct);

        if (next is null)
        {
            return null;
        }

        var expiryDays = await _policies.GetIntAsync(LibraryPolicy.Keys.ReservationExpiryDays, 3, ct);
        var now = DateTime.UtcNow;

        next.Status = ReservationStatus.Available;
        next.ReservedCopyId = bookCopyId;
        next.AvailableSinceUtc = now;
        next.ExpiresUtc = now.AddDays(expiryDays);

        // The copy is held, not shelved - it must not be issued to a passer-by.
        var copy = await _db.BookCopies.FirstOrDefaultAsync(c => c.Id == bookCopyId, ct);
        if (copy is not null)
        {
            copy.Status = BookCopyStatus.Reserved;
        }

        var book = await _db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);

        _outbox.Enqueue(
            kind: NotificationKind.ReservationAvailable,
            studentId: next.StudentId,
            recipient: next.Student?.Email ?? string.Empty,
            subject: "Your reserved book is ready to collect",
            body: $"""
                   <p><strong>{book?.Title}</strong> is now available for you to collect.</p>
                   <p>Please collect it from the circulation desk by
                   {next.ExpiresUtc:dd MMMM yyyy}. After that the hold will pass to the next
                   student in the queue.</p>
                   """,
            deduplicationKey: $"reservation-available:{next.Id}",
            correlationId: null);

        _logger.LogInformation(
            "Reservation {ReservationId} fulfilled with copy {CopyId}; expires {Expiry}.",
            next.Id, bookCopyId, next.ExpiresUtc);

        return next;
    }

    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var stale = await _db.Reservations
            .Where(r => r.Status == ReservationStatus.Available && r.ExpiresUtc != null && r.ExpiresUtc < now)
            .ToListAsync(ct);

        foreach (var reservation in stale)
        {
            reservation.Status = ReservationStatus.Expired;
            reservation.CancellationReason = "Not collected before the hold expired";

            // Release the copy and offer it to whoever is next.
            if (reservation.ReservedCopyId is { } copyId)
            {
                var copy = await _db.BookCopies.FirstOrDefaultAsync(c => c.Id == copyId, ct);
                if (copy is not null && copy.Status == BookCopyStatus.Reserved)
                {
                    copy.Status = BookCopyStatus.Available;
                }

                await ResequenceAsync(reservation.BookId, ct);
                await _db.SaveChangesAsync(ct);
                await FulfilNextAsync(reservation.BookId, copyId, ct);
            }
        }

        if (stale.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return stale.Count;
    }

    /// <summary>Closes gaps so queue positions stay contiguous and meaningful to students.</summary>
    private async Task ResequenceAsync(int bookId, CancellationToken ct)
    {
        var queued = await _db.Reservations
            .Where(r => r.BookId == bookId && r.Status == ReservationStatus.Queued)
            .OrderBy(r => r.QueuePosition).ThenBy(r => r.CreatedUtc)
            .ToListAsync(ct);

        for (var i = 0; i < queued.Count; i++)
        {
            queued[i].QueuePosition = i + 1;
        }
    }
}
