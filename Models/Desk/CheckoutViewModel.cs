using Library_Management_system.Domain.Entities;

namespace Library_Management_system.Models.Desk;

public sealed class CheckoutViewModel
{
    public string? StudentTag { get; set; }
    public string? BookTag { get; set; }

    public Student? Student { get; set; }
    public BookCopy? Copy { get; set; }

    public string? StudentTagError { get; set; }
    public string? BookTagError { get; set; }

    public int LoanDays { get; set; }
    public int MaximumLoanDays { get; set; }
    public string Currency { get; set; } = "PKR";

    public bool IsEligible { get; set; }
    public IReadOnlyList<string> EligibilityMessages { get; set; } = [];

    public bool? Succeeded { get; set; }
    public string? ResultMessage { get; set; }
    public string? TransactionNumber { get; set; }
    public DateTime? DueUtc { get; set; }

    public bool BothScanned => Student is not null && Copy is not null;

    /// <summary>Loan periods offered at the desk, capped by policy (specification section 15, step 12).</summary>
    public IEnumerable<int> LoanOptions =>
        new[] { 7, 14, 21, 30 }.Where(d => d <= MaximumLoanDays);

    /// <summary>
    /// Most precise location available. Section 9 requires showing what is known rather than
    /// inventing precision.
    /// </summary>
    public string LocationText
    {
        get
        {
            if (Copy is null) return "—";

            var parts = new List<string>();
            if (Copy.LibrarySection?.Name is { Length: > 0 } section) parts.Add(section);
            if (Copy.Shelf?.Name is { Length: > 0 } shelf) parts.Add($"Shelf {shelf}");
            if (Copy.ShelfPosition is not null) parts.Add($"Position {Copy.ShelfPosition.Position}");

            return parts.Count > 0 ? string.Join(" • ", parts) : "Location not recorded";
        }
    }
}

public sealed class CheckoutSubmission
{
    public string? StudentTag { get; set; }
    public string? BookTag { get; set; }
    public int? LoanDays { get; set; }
}

/// <summary>
/// Return screen. Section 19 allows returning with the book tag alone, so the student is
/// discovered from the loan rather than required up front.
/// </summary>
public sealed class ReturnViewModel
{
    public string? BookTag { get; set; }
    public string? LookupError { get; set; }

    public BookCopy? Copy { get; set; }
    public Student? Borrower { get; set; }
    public DateTime? DueDate { get; set; }
    public string? TransactionNumber { get; set; }

    /// <summary>Projected fine if returned now, shown before the librarian confirms.</summary>
    public int ProjectedOverdueDays { get; set; }
    public decimal ProjectedFine { get; set; }
    public string Currency { get; set; } = "PKR";

    public bool? Succeeded { get; set; }
    public string? ResultMessage { get; set; }

    public bool HasActiveLoan => Copy is not null && Borrower is not null;
    public bool IsOverdue => ProjectedOverdueDays > 0;
}

/// <summary>One reservation on the librarian queue view (specification section 26).</summary>
public sealed class ReservationLine
{
    public int Id { get; set; }
    public string BookTitle { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string RollNumber { get; set; } = string.Empty;
    public int QueuePosition { get; set; }
    public bool IsReadyToCollect { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public string? HeldCopy { get; set; }

    /// <summary>A hold past its collection deadline is occupying a copy nobody can borrow.</summary>
    public bool IsOverdueForCollection =>
        IsReadyToCollect && ExpiresUtc is not null && ExpiresUtc < DateTime.UtcNow;

    public int DaysWaiting => (int)(DateTime.UtcNow.Date - CreatedUtc.Date).TotalDays;
}

/// <summary>One row on the live scan monitor (specification section 47).</summary>
public sealed class ScanLine
{
    public string Epc { get; set; } = string.Empty;
    public string ReaderName { get; set; } = string.Empty;
    public DateTime ObservedUtc { get; set; }
    public int ReadCount { get; set; }
    public int? Rssi { get; set; }
    public int? Antenna { get; set; }
    public string Kind { get; set; } = "Unknown";
    public string? Resolved { get; set; }

    public bool IsUnknown => Resolved is null;
}

/// <summary>RFID tag assignment (specification sections 36, 37, 4F).</summary>
public sealed class TagAssignmentViewModel
{
    public string? StudentQuery { get; set; }
    public string? BookQuery { get; set; }

    public string? ScannedEpc { get; set; }
    public bool ScannedTagIsKnown { get; set; }
    public string? ScannedTagHolder { get; set; }

    public IReadOnlyList<Student> Students { get; set; } = [];
    public IReadOnlyList<BookCopy> Copies { get; set; } = [];

    public bool? Succeeded { get; set; }
    public string? ResultMessage { get; set; }
    public string? ConflictHolder { get; set; }

    public static string? ActiveEpc(Student student) =>
        student.RfidTags.FirstOrDefault(t => t.IsActive)?.Epc;

    public static string? ActiveEpc(BookCopy copy) =>
        copy.RfidTags.FirstOrDefault(t => t.IsActive)?.Epc;
}

/// <summary>Manual fallback when RFID is unavailable (specification sections 20, 21, 99).</summary>
public sealed class ManualIssueViewModel
{
    public string? StudentQuery { get; set; }
    public string? BookQuery { get; set; }

    public IReadOnlyList<Student> StudentMatches { get; set; } = [];
    public IReadOnlyList<BookCopy> CopyMatches { get; set; } = [];

    public int? SelectedStudentId { get; set; }
    public int? SelectedCopyId { get; set; }

    public Student? SelectedStudent { get; set; }
    public BookCopy? SelectedCopy { get; set; }

    public int LoanDays { get; set; }
    public int MaximumLoanDays { get; set; }

    public bool IsEligible { get; set; }
    public IReadOnlyList<string> EligibilityMessages { get; set; } = [];

    public bool? Succeeded { get; set; }
    public string? ResultMessage { get; set; }
    public string? TransactionNumber { get; set; }

    public bool BothSelected => SelectedStudent is not null && SelectedCopy is not null;

    public IEnumerable<int> LoanOptions =>
        new[] { 7, 14, 21, 30 }.Where(d => d <= MaximumLoanDays);
}
