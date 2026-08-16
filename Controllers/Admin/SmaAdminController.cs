using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Controllers.Admin;

/// <summary>
/// SMA admin dashboard and policy management (specification sections 32, 34, 68, 100).
///
/// Deliberately separate from the inherited /admin/dashboard, which still works. Sections 68 and
/// 101 want configuration and monitoring kept away from day-to-day librarian workflow, so this
/// covers what the librarian screens do not.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/sma")]
public class SmaAdminController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ILibraryPolicyService _policies;

    public SmaAdminController(ApplicationDbContext db, ILibraryPolicyService policies)
    {
        _db = db;
        _policies = policies;
    }

    [HttpGet("")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var today = DateTime.UtcNow.Date;
        var policy = await _policies.GetLoanPolicyAsync();

        var copies = _db.BookCopies.AsNoTracking().Where(c => c.CopyNumber != "LEGACY");
        var loans = _db.BorrowingRecords.AsNoTracking();

        var model = new AdminDashboardViewModel
        {
            Currency = policy.Currency,

            TotalTitles = await _db.Books.AsNoTracking().CountAsync(),
            TotalCopies = await copies.CountAsync(),
            AvailableCopies = await copies.CountAsync(c => c.Status == BookCopyStatus.Available),
            IssuedCopies = await copies.CountAsync(c => c.Status == BookCopyStatus.Issued),
            LostOrDamagedCopies = await copies.CountAsync(c =>
                c.Status == BookCopyStatus.Lost
                || c.Status == BookCopyStatus.Damaged
                || c.Status == BookCopyStatus.Missing),

            TotalStudents = await _db.Students.AsNoTracking().CountAsync(),
            ActiveStudents = await _db.Students.AsNoTracking()
                .CountAsync(s => s.Status == StudentStatus.Active),

            OverdueLoans = await loans.CountAsync(r => r.ReturnDate == null && r.DueDate < today),
            ActiveLoans = await loans.CountAsync(r => r.ReturnDate == null),
            IssuedToday = await loans.CountAsync(r => r.BorrowDate >= today),
            ReturnedToday = await loans.CountAsync(r => r.ReturnDate != null && r.ReturnDate >= today),

            OutstandingFines = await _db.Fines.AsNoTracking()
                .Where(f => !f.Paid).SumAsync(f => (decimal?)f.Amount) ?? 0m,

            ReadersOnline = await _db.RfidReaders.AsNoTracking()
                .CountAsync(r => r.Status == RfidReaderStatus.Online),
            ReadersOffline = await _db.RfidReaders.AsNoTracking()
                .CountAsync(r => r.Status != RfidReaderStatus.Online),

            PendingNotifications = await _db.Notifications.AsNoTracking()
                .CountAsync(n => n.Status == NotificationStatus.Pending),
            FailedNotifications = await _db.Notifications.AsNoTracking()
                .CountAsync(n => n.Status == NotificationStatus.Abandoned),

            UnacknowledgedSecurityEvents = await _db.SecurityEvents.AsNoTracking()
                .CountAsync(e => !e.IsAcknowledged),

            // Students still on PENDING- identifiers from the Phase 3 backfill need an
            // administrator to complete their real roll numbers (section 35).
            StudentsNeedingDetails = await _db.Students.AsNoTracking()
                .CountAsync(s => s.RollNumber.StartsWith("PENDING-"))
        };

        model.RecentLoans = await loans
            .Where(r => r.ReturnDate == null)
            .OrderByDescending(r => r.BorrowDate)
            .Take(8)
            .Select(r => new AdminDashboardViewModel.LoanLine
            {
                TransactionNumber = r.TransactionNumber,
                StudentName = r.Student!.FullName,
                Title = r.Book!.Title,
                DueDate = r.DueDate,
                Method = r.IssueMethod.ToString()
            })
            .ToListAsync();

        return View("~/Views/Admin/SmaDashboard.cshtml", model);
    }

    [HttpGet("policies")]
    public async Task<IActionResult> Policies()
    {
        var policies = await _db.LibraryPolicies
            .AsNoTracking()
            .OrderBy(p => p.Category).ThenBy(p => p.Key)
            .ToListAsync();

        return View("~/Views/Admin/SmaPolicies.cshtml", policies);
    }

    [HttpPost("policies")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePolicy(int id, string value)
    {
        var policy = await _db.LibraryPolicies.FirstOrDefaultAsync(p => p.Id == id);
        if (policy is null)
        {
            return NotFound();
        }

        policy.Value = value?.Trim() ?? string.Empty;
        policy.UpdatedBy = User.Identity?.Name;
        policy.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // The policy service caches for five minutes; a change must take effect immediately.
        _policies.Invalidate();

        TempData["PolicySaved"] = $"{policy.Key} updated.";
        return RedirectToAction(nameof(Policies));
    }
}
