using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Models;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Circulation;

/// <summary>
/// The single issue/return path (specification section 71).
///
/// RFID checkout and manual librarian checkout BOTH call this. Specification section 87 forbids
/// duplicating the rules between the two workflows, so there is exactly one implementation and the
/// entry point differs only by <see cref="CirculationMethod"/>.
/// </summary>
public interface ICirculationService
{
    Task<EligibilityResult> ValidateIssueAsync(int studentId, int bookCopyId, int? requestedLoanDays, CancellationToken ct = default);
    Task<IssueResult> IssueBookAsync(IssueRequest request, CancellationToken ct = default);
    Task<ReturnResult> ReturnBookAsync(ReturnRequest request, CancellationToken ct = default);

    /// <summary>Pure calculation, exposed so the UI can preview a due date before committing.</summary>
    Task<DateTime> CalculateDueDateAsync(DateTime issuedUtc, int? requestedLoanDays, CancellationToken ct = default);

    /// <summary>Pure calculation. Overdue days and the resulting fine under current policy.</summary>
    Task<(int OverdueDays, decimal Fine)> CalculateFineAsync(DateTime dueUtc, DateTime returnedUtc, CancellationToken ct = default);
}

public sealed class CirculationService : ICirculationService
{
    private readonly ApplicationDbContext _db;
    private readonly ILibraryPolicyService _policies;
    private readonly IReservationService _reservations;
    private readonly ILogger<CirculationService> _logger;

    public CirculationService(
        ApplicationDbContext db,
        ILibraryPolicyService policies,
        IReservationService reservations,
        ILogger<CirculationService> logger)
    {
        _db = db;
        _policies = policies;
        _reservations = reservations;
        _logger = logger;
    }

    // ---------------------------------------------------------------- calculations

    public async Task<DateTime> CalculateDueDateAsync(
        DateTime issuedUtc, int? requestedLoanDays, CancellationToken ct = default)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);
        var days = ResolveLoanDays(requestedLoanDays, policy);
        return issuedUtc.Date.AddDays(days);
    }

    private static int ResolveLoanDays(int? requested, LoanPolicySnapshot policy)
    {
        var days = requested ?? policy.DefaultLoanDays;
        return Math.Clamp(days, 1, policy.MaximumLoanDays);
    }

    public async Task<(int OverdueDays, decimal Fine)> CalculateFineAsync(
        DateTime dueUtc, DateTime returnedUtc, CancellationToken ct = default)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);
        return CalculateFine(dueUtc, returnedUtc, policy);
    }

    /// <summary>
    /// Whole days late, measured on date boundaries so a book due Monday and returned Monday
    /// evening is not "late". The grace period is subtracted before charging, and a fine is only
    /// charged on days beyond it.
    /// </summary>
    internal static (int OverdueDays, decimal Fine) CalculateFine(
        DateTime dueUtc, DateTime returnedUtc, LoanPolicySnapshot policy)
    {
        var overdueDays = (int)(returnedUtc.Date - dueUtc.Date).TotalDays;
        if (overdueDays <= 0)
        {
            return (0, 0m);
        }

        var chargeableDays = overdueDays - policy.GracePeriodDays;
        if (chargeableDays <= 0)
        {
            return (overdueDays, 0m);
        }

        return (overdueDays, chargeableDays * policy.FinePerDay);
    }

    // ---------------------------------------------------------------- eligibility

    public async Task<EligibilityResult> ValidateIssueAsync(
        int studentId, int bookCopyId, int? requestedLoanDays, CancellationToken ct = default)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);
        var refusals = new List<CirculationRefusal>();

        var student = await _db.Students.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == studentId, ct);

        if (student is null)
        {
            return EligibilityResult.Refused(new CirculationRefusal(
                CirculationFailure.StudentNotFound,
                "That student card is not registered. Ask the librarian to register or replace it."));
        }

        var copy = await _db.BookCopies.AsNoTracking()
            .Include(c => c.Book)
            .FirstOrDefaultAsync(c => c.Id == bookCopyId, ct);

        if (copy is null)
        {
            return EligibilityResult.Refused(new CirculationRefusal(
                CirculationFailure.CopyNotFound,
                "That book tag is not registered in the catalogue."));
        }

        // ---- student state ----
        if (student.Status != StudentStatus.Active)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.StudentInactive,
                $"This account is {student.Status.ToString().ToLowerInvariant()} and cannot borrow."));
        }

        if (student.IsBorrowingBlocked)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.StudentBlocked,
                string.IsNullOrWhiteSpace(student.BorrowingBlockReason)
                    ? "Borrowing is blocked on this account. Please see the librarian."
                    : $"Borrowing is blocked: {student.BorrowingBlockReason}"));
        }

        // ---- copy state ----
        if (copy.Status == BookCopyStatus.Issued)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.CopyAlreadyIssued,
                "This copy is already on loan to someone else."));
        }
        else if (!copy.IsBorrowable)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.CopyNotAvailable,
                $"This copy is not available for borrowing (status: {copy.Status})."));
        }

        // ---- the student's current position ----
        var openLoans = await _db.BorrowingRecords.AsNoTracking()
            .Where(r => r.StudentId == studentId && r.ReturnDate == null)
            .Select(r => new { r.BookId, r.DueDate })
            .ToListAsync(ct);

        if (openLoans.Count >= policy.MaximumBooksPerStudent)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.BorrowingLimitReached,
                $"You already have {openLoans.Count} books out; the limit is {policy.MaximumBooksPerStudent}."));
        }

        var today = DateTime.UtcNow.Date;
        var overdueCount = openLoans.Count(l => l.DueDate.Date < today);
        if (overdueCount > policy.MaximumOverdueBooks)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.TooManyOverdue,
                $"You cannot borrow because your account has {overdueCount} overdue books."));
        }

        // Borrowing a second copy of a title you already hold is pointless and usually a mis-scan.
        if (openLoans.Any(l => l.BookId == copy.BookId))
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.AlreadyHasThisTitle,
                "You already have a copy of this title on loan."));
        }

        // ---- fines ----
        var outstanding = await GetOutstandingFineAsync(studentId, ct);
        if (outstanding > policy.MaximumOutstandingFine)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.OutstandingFineTooHigh,
                $"Outstanding fines of {policy.Currency} {outstanding:0.00} exceed the "
                + $"{policy.Currency} {policy.MaximumOutstandingFine:0.00} limit. Please settle at the desk."));
        }

        // ---- requested period ----
        if (requestedLoanDays is > 0 && requestedLoanDays > policy.MaximumLoanDays)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.LoanPeriodInvalid,
                $"The longest loan period is {policy.MaximumLoanDays} days."));
        }

        if (requestedLoanDays is <= 0)
        {
            refusals.Add(new CirculationRefusal(
                CirculationFailure.LoanPeriodInvalid,
                "Choose a loan period of at least one day."));
        }

        return refusals.Count == 0
            ? EligibilityResult.Eligible()
            : new EligibilityResult(false, refusals);
    }

    private async Task<decimal> GetOutstandingFineAsync(int studentId, CancellationToken ct)
    {
        // The inherited fines table links to a borrowing record and carries a Paid flag.
        return await _db.Fines.AsNoTracking()
            .Where(f => !f.Paid
                        && f.Borrowing != null
                        && f.Borrowing.StudentId == studentId)
            .SumAsync(f => (decimal?)f.Amount, ct) ?? 0m;
    }

    // ---------------------------------------------------------------- issue

    public async Task<IssueResult> IssueBookAsync(IssueRequest request, CancellationToken ct = default)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);
        var loanDays = ResolveLoanDays(request.RequestedLoanDays, policy);

        var eligibility = await ValidateIssueAsync(
            request.StudentId, request.BookCopyId, request.RequestedLoanDays ?? loanDays, ct);

        if (!eligibility.IsEligible)
        {
            return IssueResult.Failure([.. eligibility.Refusals]);
        }

        var issuedUtc = DateTime.UtcNow;
        var dueUtc = issuedUtc.Date.AddDays(loanDays);

        // The connection is configured with EnableRetryOnFailure, which forbids a manually
        // started transaction unless the whole unit runs inside the execution strategy - the
        // strategy has to be able to replay everything, not half of it.
        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
        // A retry replays this block, so anything tracked by a failed attempt must go first,
        // or the record would be inserted twice.
        _db.ChangeTracker.Clear();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            var copy = await _db.BookCopies.FirstAsync(c => c.Id == request.BookCopyId, ct);
            var student = await _db.Students.FirstAsync(s => s.Id == request.StudentId, ct);

            var record = new BorrowingRecord
            {
                StudentId = student.Id,
                Username = student.Email ?? student.RollNumber,
                BookId = copy.BookId,
                BookCopyId = copy.Id,
                BorrowDate = issuedUtc,
                DueDate = dueUtc,
                DurationDays = loanDays,
                Status = "active",
                Source = request.Method == CirculationMethod.Rfid ? "rfid" : "in_person",
                IssueMethod = request.Method,
                IssueReaderId = request.ReaderId,
                CreatedBy = request.OperatorUserId,
                CreatedDate = issuedUtc
            };

            _db.BorrowingRecords.Add(record);

            copy.Status = BookCopyStatus.Issued;
            copy.LastSeenUtc = issuedUtc;
            copy.LastSeenReaderId = request.ReaderId;

            await _db.SaveChangesAsync(ct);

            // Transaction number needs the identity value, so it is assigned after the insert.
            record.TransactionNumber = BuildTransactionNumber(issuedUtc, record.Id);
            await _db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Issued copy {CopyId} to student {StudentId} as {TransactionNumber} via {Method} (correlation {CorrelationId}).",
                copy.Id, student.Id, record.TransactionNumber, request.Method, request.CorrelationId);

            return new IssueResult(true, record.TransactionNumber, record.Id, issuedUtc, dueUtc, loanDays, []);
        }
        catch (DbUpdateException ex) when (IsDuplicateActiveLoan(ex))
        {
            // The unique filtered index caught a race that eligibility could not: two operators,
            // or a duplicate RFID scan, issuing the same copy at the same moment.
            await transaction.RollbackAsync(ct);

            _logger.LogWarning(ex,
                "Concurrent issue rejected for copy {CopyId} (correlation {CorrelationId}).",
                request.BookCopyId, request.CorrelationId);

            return IssueResult.Failure(new CirculationRefusal(
                CirculationFailure.CopyAlreadyIssued,
                "This copy was issued a moment ago by someone else. Please scan it again to confirm."));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await transaction.RollbackAsync(ct);

            _logger.LogWarning(ex,
                "Concurrency conflict issuing copy {CopyId} (correlation {CorrelationId}).",
                request.BookCopyId, request.CorrelationId);

            return IssueResult.Failure(new CirculationRefusal(
                CirculationFailure.ConcurrencyConflict,
                "That book's record changed while this was being processed. Please try again."));
        }
        });
    }

    // ---------------------------------------------------------------- return

    public async Task<ReturnResult> ReturnBookAsync(ReturnRequest request, CancellationToken ct = default)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);

        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
        _db.ChangeTracker.Clear();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            var loan = await _db.BorrowingRecords
                .FirstOrDefaultAsync(r => r.BookCopyId == request.BookCopyId && r.ReturnDate == null, ct);

            if (loan is null)
            {
                await transaction.RollbackAsync(ct);
                return ReturnResult.Failure(new CirculationRefusal(
                    CirculationFailure.NoActiveLoanForCopy,
                    "This copy is not currently on loan, so there is nothing to return."));
            }

            // Section 19 permits book-tag-only returns; when a student is supplied it must match.
            if (request.StudentId is { } studentId && loan.StudentId != studentId)
            {
                await transaction.RollbackAsync(ct);
                return ReturnResult.Failure(new CirculationRefusal(
                    CirculationFailure.NotBorrowedByThisStudent,
                    "This book was borrowed on a different account. The librarian can still accept the return."));
            }

            var returnedUtc = DateTime.UtcNow;
            var (overdueDays, fineAmount) = CalculateFine(loan.DueDate, returnedUtc, policy);

            loan.ReturnDate = returnedUtc;
            loan.Status = "returned";
            loan.ReturnMethod = request.Method;
            loan.ReturnReaderId = request.ReaderId;
            loan.ReturnUserId = request.OperatorUserId;

            var copy = await _db.BookCopies.FirstAsync(c => c.Id == request.BookCopyId, ct);
            copy.Status = BookCopyStatus.Available;
            copy.LastSeenUtc = returnedUtc;
            copy.LastSeenReaderId = request.ReaderId;

            // A returned copy belongs to whoever is waiting for it, before it goes back on the
            // shelf (specification section 26). This runs inside the same transaction, so the
            // hold and the return commit together.
            var heldFor = await _reservations.FulfilNextAsync(copy.BookId, copy.Id, ct);
            if (heldFor is not null)
            {
                _logger.LogInformation(
                    "Copy {CopyId} held for reservation {ReservationId} rather than returned to the shelf.",
                    copy.Id, heldFor.Id);
            }

            if (fineAmount > 0m)
            {
                _db.Fines.Add(new Fine
                {
                    BorrowID = loan.Id,
                    Amount = fineAmount,
                    Paid = false,
                    Remark = $"{overdueDays} day(s) overdue at {policy.Currency} {policy.FinePerDay:0.00}/day."
                });
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Returned copy {CopyId} on {TransactionNumber}; overdue {OverdueDays} day(s), fine {Fine}.",
                copy.Id, loan.TransactionNumber, overdueDays, fineAmount);

            return new ReturnResult(
                true, loan.TransactionNumber, returnedUtc, overdueDays, fineAmount, policy.Currency, []);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await transaction.RollbackAsync(ct);

            _logger.LogWarning(ex, "Concurrency conflict returning copy {CopyId}.", request.BookCopyId);

            return ReturnResult.Failure(new CirculationRefusal(
                CirculationFailure.ConcurrencyConflict,
                "That book's record changed while this was being processed. Please try again."));
        }
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>SMA-LIB-2026-000123 (specification section 41).</summary>
    internal static string BuildTransactionNumber(DateTime issuedUtc, int recordId) =>
        $"SMA-LIB-{issuedUtc:yyyy}-{recordId:D6}";

    /// <summary>
    /// Recognises a violation of UX_BorrowingRecords_OneOpenLoanPerCopy specifically, so a genuine
    /// race is reported as "already issued" rather than as an opaque database error.
    /// </summary>
    private static bool IsDuplicateActiveLoan(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UX_BorrowingRecords_OneOpenLoanPerCopy",
            StringComparison.OrdinalIgnoreCase) == true;
}
