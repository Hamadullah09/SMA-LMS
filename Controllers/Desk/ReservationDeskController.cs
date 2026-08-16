using Library_Management_system.Application.Circulation;
using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Controllers.Desk;

/// <summary>
/// Librarian view of the reservation queues (specification section 26).
///
/// The desk needs two things the student portal does not: which holds are waiting to be collected
/// (a shelf of held books nobody has come for is a real operational problem), and the ability to
/// release a hold that is blocking a copy.
/// </summary>
[Authorize(Roles = "Admin,Librarian")]
[Route("desk/reservations")]
public class ReservationDeskController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IReservationService _reservations;

    public ReservationDeskController(ApplicationDbContext db, IReservationService reservations)
    {
        _db = db;
        _reservations = reservations;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var rows = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.Status == ReservationStatus.Queued || r.Status == ReservationStatus.Available)
            .OrderBy(r => r.Status == ReservationStatus.Available ? 0 : 1)
            .ThenBy(r => r.ExpiresUtc)
            .ThenBy(r => r.BookId).ThenBy(r => r.QueuePosition)
            .Select(r => new Models.Desk.ReservationLine
            {
                Id = r.Id,
                BookTitle = r.Book!.Title,
                StudentName = r.Student!.FullName,
                RollNumber = r.Student.RollNumber,
                QueuePosition = r.QueuePosition,
                IsReadyToCollect = r.Status == ReservationStatus.Available,
                CreatedUtc = r.CreatedUtc,
                ExpiresUtc = r.ExpiresUtc,
                HeldCopy = r.ReservedCopy == null ? null : r.ReservedCopy.CopyNumber
            })
            .ToListAsync();

        return View("~/Views/Desk/Reservations.cshtml", rows);
    }

    /// <summary>
    /// Release a hold on the student's behalf. Uses the same service the student portal would,
    /// so re-sequencing of the remaining queue is identical.
    /// </summary>
    [HttpPost("release")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Release(int id)
    {
        var reservation = await _db.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (reservation is null)
        {
            TempData["ReservationMessage"] = "That reservation no longer exists.";
            return RedirectToAction(nameof(Index));
        }

        var result = await _reservations.CancelAsync(id, reservation.StudentId);

        // A held copy must go back on the shelf, otherwise releasing the hold strands it.
        if (result.Succeeded && reservation.ReservedCopyId is { } copyId)
        {
            var copy = await _db.BookCopies.FirstOrDefaultAsync(c => c.Id == copyId);
            if (copy is not null && copy.Status == BookCopyStatus.Reserved)
            {
                copy.Status = BookCopyStatus.Available;
                await _db.SaveChangesAsync();
            }
        }

        TempData["ReservationMessage"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Sweeps holds nobody collected, passing each to the next student.</summary>
    [HttpPost("expire")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExpireStale()
    {
        var count = await _reservations.ExpireStaleAsync();

        TempData["ReservationMessage"] = count == 0
            ? "No holds have expired."
            : $"{count} uncollected hold(s) expired and passed to the next student in the queue.";

        return RedirectToAction(nameof(Index));
    }
}
