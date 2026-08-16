using Library_Management_system.Application.Circulation;
using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Models.Portal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Controllers.Portal;

/// <summary>
/// The redesigned catalogue (specification sections 11, 94, 95).
///
/// Availability comes from BookCopy rows rather than the inherited Book.Quantity scalar - the copy
/// table is now the source of truth for what physically exists and what is on the shelf.
///
/// Performance (section 49): server-side paging, AsNoTracking throughout, and projection to view
/// models so entire entity graphs are never materialised to render a grid.
/// </summary>
[Route("catalogue")]
public class CatalogueController : Controller
{
    private const int PageSize = 12;

    private readonly ApplicationDbContext _db;
    private readonly ILibraryPolicyService _policies;
    private readonly IReservationService _reservations;
    private readonly Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser> _userManager;

    public CatalogueController(
        ApplicationDbContext db,
        ILibraryPolicyService policies,
        IReservationService reservations,
        Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser> userManager)
    {
        _db = db;
        _policies = policies;
        _reservations = reservations;
        _userManager = userManager;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? q, string? category, bool availableOnly = false, string sort = "title", int page = 1)
    {
        page = Math.Max(1, page);

        var model = new CatalogueViewModel
        {
            Query = q,
            Category = category,
            AvailableOnly = availableOnly,
            Sort = sort,
            Page = page,
            PageSize = PageSize
        };

        var books = _db.Books.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            books = books.Where(b =>
                b.Title.Contains(term) ||
                b.Author.Contains(term) ||
                b.Isbn!.Contains(term) ||
                b.CategoryName.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            books = books.Where(b => b.CategoryName == category);
        }

        // Project availability from the copy table in the same query - no N+1 (section 49).
        var projected = books.Select(b => new CatalogueCard
        {
            BookId = b.Id,
            Title = b.Title,
            Author = b.Author,
            CategoryName = b.CategoryName,
            CoverImage = b.BookImage ?? b.ImageUrl,
            Year = b.Year,
            TotalCopies = _db.BookCopies.Count(c => c.BookId == b.Id && c.CopyNumber != "LEGACY"),
            AvailableCopies = _db.BookCopies.Count(c =>
                c.BookId == b.Id && c.CopyNumber != "LEGACY" && c.Status == BookCopyStatus.Available)
        });

        if (availableOnly)
        {
            projected = projected.Where(c => c.AvailableCopies > 0);
        }

        projected = sort switch
        {
            "author" => projected.OrderBy(c => c.Author).ThenBy(c => c.Title),
            "year" => projected.OrderByDescending(c => c.Year).ThenBy(c => c.Title),
            "availability" => projected.OrderByDescending(c => c.AvailableCopies).ThenBy(c => c.Title),
            _ => projected.OrderBy(c => c.Title)
        };

        model.TotalCount = await projected.CountAsync();
        model.Results = await projected.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        model.Categories = await _db.Books
            .AsNoTracking()
            .Where(b => b.CategoryName != "")
            .Select(b => b.CategoryName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();

        return View("~/Views/Portal/Catalogue.cshtml", model);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id)
    {
        var book = await _db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (book is null)
        {
            return NotFound();
        }

        var policy = await _policies.GetLoanPolicyAsync();

        var copies = await _db.BookCopies
            .AsNoTracking()
            .Include(c => c.LibrarySection)
            .Include(c => c.Shelf)
            .Include(c => c.ShelfPosition)
            .Where(c => c.BookId == id && c.CopyNumber != "LEGACY")
            .OrderBy(c => c.CopyNumber)
            .ToListAsync();

        // When is each unavailable copy coming back? Answered from the open loan.
        var dueDates = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.ReturnDate == null && r.BookCopyId != null
                        && copies.Select(c => c.Id).Contains(r.BookCopyId.Value))
            .ToDictionaryAsync(r => r.BookCopyId!.Value, r => r.DueDate);

        var model = new BookDetailViewModel
        {
            BookId = book.Id,
            Title = book.Title,
            Author = book.Author,
            Isbn = book.Isbn,
            CategoryName = book.CategoryName,
            Description = book.Description ?? book.Summarized,
            CoverImage = book.BookImage ?? book.ImageUrl,
            Year = book.Year,
            Pages = book.Pages,
            MaximumLoanDays = policy.MaximumLoanDays,
            Copies = copies.Select(c => new BookDetailViewModel.CopyLine
            {
                CopyNumber = c.CopyNumber,
                AccessionNumber = c.AccessionNumber,
                Status = c.Status.ToString(),
                IsAvailable = c.Status == BookCopyStatus.Available,
                Location = DescribeLocation(c),
                DueBack = dueDates.TryGetValue(c.Id, out var due) ? due : null
            }).ToList()
        };

        return View("~/Views/Portal/BookDetail.cshtml", model);
    }

    /// <summary>
    /// Place a hold (specification section 26). Requires sign-in: a reservation is a queue
    /// position tied to a person, unlike browsing which is open.
    /// </summary>
    [HttpPost("{id:int}/reserve")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reserve(int id)
    {
        var userId = _userManager.GetUserId(User);

        // The student is derived from the signed-in account, never from a form field, so one
        // student cannot reserve on another's behalf (specification section 43).
        var student = await _db.Students
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ApplicationUserId == userId);

        if (student is null)
        {
            TempData["ReserveError"] =
                "Your student record is not linked yet, so reservations are not available. Please see a librarian.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await _reservations.ReserveAsync(student.Id, id);

        if (result.Succeeded)
        {
            TempData["ReserveMessage"] = result.Message;
        }
        else
        {
            TempData["ReserveError"] = result.Message;
        }

        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>Most precise location available; never invents precision (specification section 9).</summary>
    private static string DescribeLocation(Domain.Entities.BookCopy copy)
    {
        var parts = new List<string>();
        if (copy.LibrarySection?.Name is { Length: > 0 } section) parts.Add(section);
        if (copy.Shelf?.Name is { Length: > 0 } shelf) parts.Add($"Shelf {shelf}");
        if (copy.ShelfPosition is not null) parts.Add($"Position {copy.ShelfPosition.Position}");

        return parts.Count > 0 ? string.Join(" • ", parts) : "Location not recorded";
    }
}
