using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Application.Circulation;

/// <summary>
/// Why an operation was refused. Kept as a code so callers can react programmatically and so the
/// user-facing wording lives in one place (specification section 48).
/// </summary>
public enum CirculationFailure
{
    None = 0,
    StudentNotFound,
    StudentInactive,
    StudentBlocked,
    CopyNotFound,
    CopyNotAvailable,
    CopyAlreadyIssued,
    AlreadyHasThisTitle,
    BorrowingLimitReached,
    TooManyOverdue,
    OutstandingFineTooHigh,
    LoanPeriodInvalid,
    ReservedByAnotherStudent,
    NoActiveLoanForCopy,
    NotBorrowedByThisStudent,
    ConcurrencyConflict
}

/// <summary>Outcome of an eligibility check. Reasons accumulate so the desk sees every blocker at once.</summary>
public sealed record EligibilityResult(bool IsEligible, IReadOnlyList<CirculationRefusal> Refusals)
{
    public static EligibilityResult Eligible() => new(true, []);

    public static EligibilityResult Refused(params CirculationRefusal[] refusals) => new(false, refusals);

    /// <summary>Single combined message suitable for a circulation-desk screen.</summary>
    public string Summary => Refusals.Count == 0
        ? "Eligible to borrow."
        : string.Join(" ", Refusals.Select(r => r.Message));
}

public sealed record CirculationRefusal(CirculationFailure Failure, string Message);

public sealed record IssueRequest(
    int StudentId,
    int BookCopyId,
    int? RequestedLoanDays,
    CirculationMethod Method,
    int? ReaderId = null,
    string? OperatorUserId = null,
    string? CorrelationId = null);

public sealed record IssueResult(
    bool Succeeded,
    string? TransactionNumber,
    int? BorrowingRecordId,
    DateTime? IssuedUtc,
    DateTime? DueUtc,
    int? LoanDays,
    IReadOnlyList<CirculationRefusal> Refusals)
{
    public static IssueResult Failure(params CirculationRefusal[] refusals) =>
        new(false, null, null, null, null, null, refusals);

    public string Summary => Succeeded
        ? $"Issued. Due {DueUtc:dd MMMM yyyy}."
        : string.Join(" ", Refusals.Select(r => r.Message));
}

public sealed record ReturnRequest(
    int BookCopyId,
    /// <summary>
    /// Optional. Section 19 allows returning with the book tag alone; when supplied the loan must
    /// belong to this student.
    /// </summary>
    int? StudentId,
    CirculationMethod Method,
    int? ReaderId = null,
    string? OperatorUserId = null,
    string? CorrelationId = null);

public sealed record ReturnResult(
    bool Succeeded,
    string? TransactionNumber,
    DateTime? ReturnedUtc,
    int OverdueDays,
    decimal FineAmount,
    string Currency,
    IReadOnlyList<CirculationRefusal> Refusals)
{
    public static ReturnResult Failure(params CirculationRefusal[] refusals) =>
        new(false, null, null, 0, 0m, "PKR", refusals);

    public bool IsLate => OverdueDays > 0;

    public string Summary => !Succeeded
        ? string.Join(" ", Refusals.Select(r => r.Message))
        : IsLate
            ? $"Returned {OverdueDays} day(s) late. Fine: {Currency} {FineAmount:0.00}."
            : "Returned on time. No fine.";
}
