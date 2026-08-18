namespace Library_Management_system.Models;

public sealed class ProfileViewModel
{
    public string FullName { get; set; } = "User";
    public string Email { get; set; } = string.Empty;
    public string MembershipLabel { get; set; } = "Member";
    /// <summary>Empty when the student has not uploaded one; the page falls back to initials.</summary>
    public string ProfileImageUrl { get; set; } = string.Empty;
    public IReadOnlyList<ProfileInterestItemViewModel> Interests { get; set; } =
        Array.Empty<ProfileInterestItemViewModel>();

    // ---- borrowing, drawn from the same records History uses ---------------
    // The page previously showed a name, an email and a bookmark count. None of that answers
    // the questions a student opens their profile to ask: what have I got out, when is it due,
    // do I owe anything.

    /// <summary>Books currently out, including overdue ones.</summary>
    public int BooksOutCount { get; set; }

    /// <summary>Of those, how many are past their due date.</summary>
    public int OverdueCount { get; set; }

    /// <summary>Loans closed by a return.</summary>
    public int ReturnedCount { get; set; }

    /// <summary>Every loan ever, returned or not.</summary>
    public int TotalBorrowedCount { get; set; }

    /// <summary>Unpaid fines, accrued and estimated, in <see cref="Currency"/>.</summary>
    public decimal OutstandingFine { get; set; }

    public string Currency { get; set; } = "PKR";

    /// <summary>How many books this student may hold at once, from library policy.</summary>
    public int BorrowLimit { get; set; }

    /// <summary>The earliest due date among books still out; null when nothing is out.</summary>
    public DateTime? NextDueDate { get; set; }

    /// <summary>Title of the book due at <see cref="NextDueDate"/>.</summary>
    public string? NextDueTitle { get; set; }

    /// <summary>Account creation date, when the store recorded one.</summary>
    public DateTime? MemberSince { get; set; }

    /// <summary>The most recent loans, for the activity list.</summary>
    public IReadOnlyList<ProfileLoanItemViewModel> RecentLoans { get; set; } =
        Array.Empty<ProfileLoanItemViewModel>();
}

public sealed class ProfileInterestItemViewModel
{
    public int BookId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public int Rating { get; set; }
}

/// <summary>One loan on the profile activity list.</summary>
public sealed class ProfileLoanItemViewModel
{
    public int BookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public DateTime BorrowDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? ReturnDate { get; set; }

    /// <summary>One of <c>returned</c>, <c>overdue</c>, <c>borrowing</c> — matches History.</summary>
    public string Status { get; set; } = "borrowing";
    public string StatusLabel { get; set; } = "Borrowing";
}
