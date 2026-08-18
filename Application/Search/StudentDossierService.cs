using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Search;

public sealed record DossierLoan(
    int Id,
    string? TransactionNumber,
    string BookTitle,
    string? Author,
    int BookId,
    string? CopyNumber,
    string? AccessionNumber,
    DateTime BorrowDate,
    DateTime DueDate,
    DateTime? ReturnDate,
    CirculationMethod IssueMethod,
    CirculationMethod? ReturnMethod,
    decimal FineAmount,
    bool FinePaid)
{
    public bool IsOut => ReturnDate is null;

    /// <summary>Days past due: for an open loan against today, for a closed one against its return.</summary>
    public int LateDays
    {
        get
        {
            var against = (ReturnDate ?? DateTime.UtcNow).Date;
            var days = (against - DueDate.Date).Days;
            return days > 0 ? days : 0;
        }
    }

    public bool IsOverdue => IsOut && LateDays > 0;
}

public sealed record DossierReservation(
    int Id,
    string BookTitle,
    int BookId,
    ReservationStatus Status,
    int QueuePosition,
    DateTime CreatedUtc,
    DateTime? ExpiresUtc);

public sealed record DossierCard(
    string Epc,
    bool IsActive,
    string State,
    DateTime? AssignedUtc);

public sealed record DossierRequest(
    int CartItemId,
    string BookTitle,
    int BookId,
    string Status,
    DateTime? RequestedDate);

/// <summary>
/// Everything the library holds about one student, on one page.
/// </summary>
/// <remarks>
/// Global search could already find a student, but the only place it could send a librarian was
/// the manual-issue screen, which shows a name and nothing else. Answering "what has this student
/// got out, what have they returned, do they owe anything, what are they waiting on" meant opening
/// four different screens and cross-referencing by hand.
///
/// Loans are matched on <c>StudentId</c> and, for older rows written before the student link
/// existed, on the free-text <c>Username</c> the desk recorded. Dropping the second match would
/// silently hide a student's older history.
/// </remarks>
public sealed record StudentDossier(
    int StudentId,
    string FullName,
    string RollNumber,
    string StudentIdNumber,
    string? Email,
    string? Phone,
    string? MaskedCnic,
    string? Department,
    string? Programme,
    int? Semester,
    StudentStatus Status,
    bool IsBorrowingBlocked,
    string? BorrowingBlockReason,
    DateTime CreatedUtc,
    IReadOnlyList<DossierCard> Cards,
    IReadOnlyList<DossierLoan> Loans,
    IReadOnlyList<DossierReservation> Reservations,
    IReadOnlyList<DossierRequest> Requests)
{
    public IReadOnlyList<DossierLoan> CurrentLoans =>
        [.. Loans.Where(l => l.IsOut).OrderBy(l => l.DueDate)];

    public IReadOnlyList<DossierLoan> ReturnedLoans =>
        [.. Loans.Where(l => !l.IsOut).OrderByDescending(l => l.ReturnDate)];

    public int OverdueCount => Loans.Count(l => l.IsOverdue);

    /// <summary>Unpaid only — a settled fine is history, not something to chase.</summary>
    public decimal OutstandingFine =>
        Loans.Where(l => !l.FinePaid).Sum(l => l.FineAmount);

    public decimal PaidFine => Loans.Where(l => l.FinePaid).Sum(l => l.FineAmount);

    public DateTime? NextDue => CurrentLoans.FirstOrDefault()?.DueDate;
}

public interface IStudentDossierService
{
    Task<StudentDossier?> GetAsync(int studentId, CancellationToken ct = default);
}

public sealed class StudentDossierService : IStudentDossierService
{
    private readonly ApplicationDbContext _db;

    public StudentDossierService(ApplicationDbContext db) => _db = db;

    public async Task<StudentDossier?> GetAsync(int studentId, CancellationToken ct = default)
    {
        var student = await _db.Students
            .AsNoTracking()
            .Include(s => s.Department)
            .Include(s => s.AcademicProgram)
            .FirstOrDefaultAsync(s => s.Id == studentId, ct);

        if (student is null)
        {
            return null;
        }

        var cards = await _db.StudentRfidTags
            .AsNoTracking()
            .Where(t => t.StudentId == studentId)
            .OrderByDescending(t => t.IsActive)
            .ThenByDescending(t => t.Id)
            .Select(t => new DossierCard(t.Epc, t.IsActive, t.State.ToString(), t.AssignedUtc))
            .ToListAsync(ct);

        // Older rows predate the StudentId link and carry only the free-text name the desk typed,
        // so both are matched or that history disappears from this page.
        var nameKeys = new[] { student.FullName, student.Email ?? string.Empty, student.RollNumber }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var loans = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.StudentId == studentId || nameKeys.Contains(r.Username))
            .OrderByDescending(r => r.BorrowDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new DossierLoan(
                r.Id,
                r.TransactionNumber,
                r.Book!.Title,
                r.Book.Author,
                r.BookId,
                r.BookCopy!.CopyNumber,
                r.BookCopy.AccessionNumber,
                r.BorrowDate,
                r.DueDate,
                r.ReturnDate,
                r.IssueMethod,
                r.ReturnMethod,
                r.Fine == null ? 0m : r.Fine.Amount,
                r.Fine != null && r.Fine.Paid))
            .ToListAsync(ct);

        var reservations = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.StudentId == studentId)
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => new DossierReservation(
                r.Id,
                r.Book!.Title,
                r.BookId,
                r.Status,
                r.QueuePosition,
                r.CreatedUtc,
                r.ExpiresUtc))
            .ToListAsync(ct);

        // The cart owner key is the Identity user id with a "user:" prefix (see
        // HomeController.ResolveCartOwnerKey) — matching the bare id finds nothing.
        var ownerKey = string.IsNullOrWhiteSpace(student.ApplicationUserId)
            ? null
            : "user:" + student.ApplicationUserId;

        var requests = ownerKey is null
            ? []
            : await _db.CartItems
                .AsNoTracking()
                .Where(ci => ci.OwnerKey == ownerKey && ci.ReservationStatus != "none")
                .OrderByDescending(ci => ci.RequestedDate)
                .Select(ci => new DossierRequest(
                    ci.Id,
                    ci.Book!.Title,
                    ci.BookId,
                    ci.ReservationStatus ?? "none",
                    ci.RequestedDate))
                .ToListAsync(ct);

        return new StudentDossier(
            student.Id,
            student.FullName,
            student.RollNumber,
            student.StudentIdNumber,
            student.Email,
            student.Phone,
            student.MaskedCnic,
            student.Department?.Name,
            student.AcademicProgram?.Name,
            student.Semester,
            student.Status,
            student.IsBorrowingBlocked,
            student.BorrowingBlockReason,
            student.CreatedUtc,
            cards,
            loans,
            reservations,
            requests);
    }
}
