using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Models.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ApplicationUser = Library_Management_system.Models.ApplicationUser;

namespace Library_Management_system.Controllers.Portal;

/// <summary>
/// The student portal (specification sections 10, 66, 93).
///
/// Answers the eight questions section 66 says a student must be able to answer without training:
/// what do I have, when is it due, do I owe anything, what happens if I am late.
///
/// Student isolation (section 43, section 7): every query is scoped to the signed-in student's own
/// record. There is no route parameter that could address another student's data.
/// </summary>
[Authorize]
[Route("portal")]
public class StudentPortalController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILibraryPolicyService _policies;

    public StudentPortalController(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        ILibraryPolicyService policies)
    {
        _db = db;
        _users = users;
        _policies = policies;
    }

    [HttpGet("")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var userId = _users.GetUserId(User);

        // Scoped to the signed-in account. Nothing the caller supplies selects the student.
        var student = await _db.Students
            .AsNoTracking()
            .Include(s => s.Department)
            .FirstOrDefaultAsync(s => s.ApplicationUserId == userId);

        var policy = await _policies.GetLoanPolicyAsync();

        var model = new StudentDashboardViewModel
        {
            DisplayName = student?.FullName ?? User.Identity?.Name ?? "Student",
            Student = student,
            Currency = policy.Currency,
            MaximumBooks = policy.MaximumBooksPerStudent,
            MaximumLoanDays = policy.MaximumLoanDays,
            FinePerDay = policy.FinePerDay
        };

        if (student is null)
        {
            // A signed-in account with no student record yet — an honest state, not an error.
            return View("~/Views/Portal/Dashboard.cshtml", model);
        }

        var loans = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.StudentId == student.Id && r.ReturnDate == null)
            .Select(r => new StudentLoanLine
            {
                TransactionNumber = r.TransactionNumber,
                Title = r.Book!.Title,
                CopyNumber = r.BookCopy!.CopyNumber,
                DueDate = r.DueDate
            })
            .OrderBy(l => l.DueDate)
            .ToListAsync();

        model.CurrentLoans = loans;

        model.OutstandingFine = await _db.Fines
            .AsNoTracking()
            .Where(f => !f.Paid && f.Borrowing != null && f.Borrowing.StudentId == student.Id)
            .SumAsync(f => (decimal?)f.Amount) ?? 0m;

        model.Reservations = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.StudentId == student.Id
                        && (r.Status == Domain.Enums.ReservationStatus.Queued
                            || r.Status == Domain.Enums.ReservationStatus.Available))
            .OrderBy(r => r.QueuePosition)
            .Select(r => new StudentReservationLine
            {
                ReservationId = r.Id,
                BookId = r.BookId,
                Title = r.Book!.Title,
                QueuePosition = r.QueuePosition,
                IsReadyToCollect = r.Status == Domain.Enums.ReservationStatus.Available,
                ExpiresUtc = r.ExpiresUtc
            })
            .ToListAsync();

        model.RecentlyReturned = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.StudentId == student.Id && r.ReturnDate != null)
            .OrderByDescending(r => r.ReturnDate)
            .Take(5)
            .Select(r => new StudentLoanLine
            {
                TransactionNumber = r.TransactionNumber,
                Title = r.Book!.Title,
                CopyNumber = r.BookCopy!.CopyNumber,
                DueDate = r.DueDate,
                ReturnedDate = r.ReturnDate
            })
            .ToListAsync();

        return View("~/Views/Portal/Dashboard.cshtml", model);
    }
}
