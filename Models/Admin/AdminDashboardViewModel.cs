namespace Library_Management_system.Models.Admin;

/// <summary>
/// Admin dashboard (specification section 32). Deliberately limited — section 32 says not to
/// overload it, so this carries stock, people, circulation, and system health, and nothing else.
/// </summary>
public sealed class AdminDashboardViewModel
{
    public string Currency { get; set; } = "PKR";

    // Stock
    public int TotalTitles { get; set; }
    public int TotalCopies { get; set; }
    public int AvailableCopies { get; set; }
    public int IssuedCopies { get; set; }
    public int LostOrDamagedCopies { get; set; }

    // People
    public int TotalStudents { get; set; }
    public int ActiveStudents { get; set; }
    public int StudentsNeedingDetails { get; set; }

    // Circulation
    public int ActiveLoans { get; set; }
    public int OverdueLoans { get; set; }
    public int IssuedToday { get; set; }
    public int ReturnedToday { get; set; }
    public decimal OutstandingFines { get; set; }

    // System health (section 77)
    public int ReadersOnline { get; set; }
    public int ReadersOffline { get; set; }
    public int PendingNotifications { get; set; }
    public int FailedNotifications { get; set; }
    public int UnacknowledgedSecurityEvents { get; set; }

    public IReadOnlyList<LoanLine> RecentLoans { get; set; } = [];

    public bool HasHealthConcerns =>
        ReadersOffline > 0 || FailedNotifications > 0 || UnacknowledgedSecurityEvents > 0;

    public sealed class LoanLine
    {
        public string? TransactionNumber { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime DueDate { get; set; }
        public string Method { get; set; } = string.Empty;

        public bool IsOverdue => DueDate.Date < DateTime.UtcNow.Date;
    }
}
