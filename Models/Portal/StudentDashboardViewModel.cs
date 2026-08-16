using Library_Management_system.Domain.Entities;

namespace Library_Management_system.Models.Portal;

public sealed class StudentLoanLine
{
    public string? TransactionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CopyNumber { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public DateTime? ReturnedDate { get; set; }

    public int DaysUntilDue => (int)(DueDate.Date - DateTime.UtcNow.Date).TotalDays;
    public bool IsOverdue => ReturnedDate is null && DaysUntilDue < 0;
    public bool IsDueSoon => ReturnedDate is null && DaysUntilDue is >= 0 and <= 3;

    /// <summary>Plain wording — section 66 requires a student to understand without training.</summary>
    public string DueDescription => ReturnedDate is not null
        ? $"Returned {ReturnedDate:dd MMM yyyy}"
        : DaysUntilDue switch
        {
            < 0 => $"{Math.Abs(DaysUntilDue)} day(s) overdue",
            0 => "Due today",
            1 => "Due tomorrow",
            _ => $"Due in {DaysUntilDue} days"
        };
}

public sealed class StudentReservationLine
{
    public int ReservationId { get; set; }
    public int BookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int QueuePosition { get; set; }
    public bool IsReadyToCollect { get; set; }
    public DateTime? ExpiresUtc { get; set; }

    public string StatusDescription => IsReadyToCollect
        ? $"Ready to collect — please collect by {ExpiresUtc:dd MMM yyyy}"
        : QueuePosition == 1
            ? "You are next in line"
            : $"Number {QueuePosition} in the queue";
}

public sealed class StudentDashboardViewModel
{
    public string DisplayName { get; set; } = "Student";
    public Student? Student { get; set; }

    public IReadOnlyList<StudentLoanLine> CurrentLoans { get; set; } = [];
    public IReadOnlyList<StudentLoanLine> RecentlyReturned { get; set; } = [];
    public IReadOnlyList<StudentReservationLine> Reservations { get; set; } = [];

    public decimal OutstandingFine { get; set; }
    public string Currency { get; set; } = "PKR";

    public int MaximumBooks { get; set; }
    public int MaximumLoanDays { get; set; }
    public decimal FinePerDay { get; set; }

    public int OverdueCount => CurrentLoans.Count(l => l.IsOverdue);
    public int DueSoonCount => CurrentLoans.Count(l => l.IsDueSoon);
    public int RemainingAllowance => Math.Max(0, MaximumBooks - CurrentLoans.Count);

    public bool HasStudentRecord => Student is not null;
}
