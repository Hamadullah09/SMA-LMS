using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Search;

public sealed record SearchHit(
    string Kind,
    string Title,
    string Subtitle,
    string Url,
    string? Badge = null,
    bool IsWarning = false);

public sealed record GlobalSearchResults(
    string Query,
    IReadOnlyList<SearchHit> Students,
    IReadOnlyList<SearchHit> Books,
    IReadOnlyList<SearchHit> Copies,
    IReadOnlyList<SearchHit> Transactions,
    IReadOnlyList<SearchHit> Tags)
{
    public int TotalCount =>
        Students.Count + Books.Count + Copies.Count + Transactions.Count + Tags.Count;

    public bool IsEmpty => TotalCount == 0;
}

/// <summary>
/// One search box across everything a librarian might have in their hand
/// (specification section 103): a student, a roll number, a book, an ISBN, a tag, or a receipt
/// with a transaction number on it.
///
/// Each category is capped and queried independently so one very common term cannot crowd the
/// others out, and every query is AsNoTracking + projected (section 49).
/// </summary>
public interface IGlobalSearchService
{
    Task<GlobalSearchResults> SearchAsync(string query, CancellationToken ct = default);
}

public sealed class GlobalSearchService : IGlobalSearchService
{
    private const int PerCategory = 5;

    private readonly ApplicationDbContext _db;

    public GlobalSearchService(ApplicationDbContext db) => _db = db;

    public async Task<GlobalSearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        var term = (query ?? string.Empty).Trim();

        if (term.Length < 2)
        {
            // One character matches almost everything and is never a real search.
            return new GlobalSearchResults(term, [], [], [], [], []);
        }

        var students = await _db.Students
            .AsNoTracking()
            .Where(s => s.FullName.Contains(term)
                        || s.RollNumber.Contains(term)
                        || s.StudentIdNumber.Contains(term)
                        || s.Email!.Contains(term))
            .OrderBy(s => s.FullName)
            .Take(PerCategory)
            .Select(s => new SearchHit(
                "Student",
                s.FullName,
                s.RollNumber,
                // Everything on file for them, rather than the manual-issue screen which
                // shows only a name.
                "/desk/student/" + s.Id,
                s.Status.ToString(),
                s.Status != StudentStatus.Active || s.IsBorrowingBlocked))
            .ToListAsync(ct);

        var books = await _db.Books
            .AsNoTracking()
            .Where(b => b.Title.Contains(term) || b.Author.Contains(term) || b.Isbn!.Contains(term))
            .OrderBy(b => b.Title)
            .Take(PerCategory)
            .Select(b => new SearchHit(
                "Book",
                b.Title,
                b.Author + (b.Isbn == null ? "" : " • ISBN " + b.Isbn),
                "/catalogue/" + b.Id,
                _db.BookCopies.Count(c => c.BookId == b.Id
                                          && c.CopyNumber != "LEGACY"
                                          && c.Status == BookCopyStatus.Available) + " available",
                false))
            .ToListAsync(ct);

        var copies = await _db.BookCopies
            .AsNoTracking()
            .Where(c => c.CopyNumber != "LEGACY" && c.AccessionNumber!.Contains(term))
            .OrderBy(c => c.AccessionNumber)
            .Take(PerCategory)
            .Select(c => new SearchHit(
                "Copy",
                c.Book!.Title + " — copy " + c.CopyNumber,
                c.AccessionNumber ?? "",
                "/catalogue/" + c.BookId,
                c.Status.ToString(),
                c.Status != BookCopyStatus.Available))
            .ToListAsync(ct);

        var transactions = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.TransactionNumber!.Contains(term))
            .OrderByDescending(r => r.BorrowDate)
            .Take(PerCategory)
            .Select(r => new SearchHit(
                "Loan",
                r.TransactionNumber!,
                r.Book!.Title + " • " + (r.Student == null ? "Unknown student" : r.Student.FullName),
                "/desk/return?bookTag=" + (r.BookCopy!.AccessionNumber ?? ""),
                r.ReturnDate == null ? "On loan" : "Returned",
                r.ReturnDate == null && r.DueDate < DateTime.UtcNow))
            .ToListAsync(ct);

        // Tags are searched last: an EPC is unambiguous, so an exact-prefix match is enough.
        var upper = term.ToUpperInvariant();

        var studentTags = await _db.StudentRfidTags
            .AsNoTracking()
            .Where(t => t.Epc.StartsWith(upper))
            .OrderByDescending(t => t.IsActive)
            .Take(PerCategory)
            .Select(t => new SearchHit(
                "Card",
                t.Epc,
                t.Student!.FullName + " (" + t.Student.RollNumber + ")",
                "/desk/tags?epc=" + t.Epc,
                t.IsActive ? "Active" : t.State.ToString(),
                !t.IsActive))
            .ToListAsync(ct);

        var bookTags = await _db.BookRfidTags
            .AsNoTracking()
            .Where(t => t.Epc.StartsWith(upper))
            .OrderByDescending(t => t.IsActive)
            .Take(PerCategory)
            .Select(t => new SearchHit(
                "Book tag",
                t.Epc,
                t.BookCopy!.Book!.Title + " — copy " + t.BookCopy.CopyNumber,
                "/desk/tags?epc=" + t.Epc,
                t.IsActive ? "Active" : t.State.ToString(),
                !t.IsActive))
            .ToListAsync(ct);

        return new GlobalSearchResults(
            term, students, books, copies, transactions,
            [.. studentTags, .. bookTags]);
    }
}
