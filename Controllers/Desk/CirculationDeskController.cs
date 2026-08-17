using Library_Management_system.Application.Circulation;
using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Models.Desk;
using Library_Management_system.Rfid;
using Library_Management_system.Rfid.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Controllers.Desk;

/// <summary>
/// The circulation desk (specification sections 46, 96, 97, 99).
///
/// Thin by design (section 69): every decision belongs to ICirculationService, which is the same
/// service the RFID pipeline calls. This controller resolves scanned tags to entities and renders.
/// </summary>
[Authorize(Roles = "Admin,Librarian")]
[Route("desk")]
public class CirculationDeskController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICirculationService _circulation;
    private readonly ILibraryPolicyService _policies;

    public CirculationDeskController(
        ApplicationDbContext db,
        ICirculationService circulation,
        ILibraryPolicyService policies)
    {
        _db = db;
        _circulation = circulation;
        _policies = policies;
    }

    [HttpGet("checkout")]
    public async Task<IActionResult> Checkout(string? studentTag, string? bookTag, int? loanDays)
    {
        var model = await BuildCheckoutAsync(studentTag, bookTag, loanDays);
        return View("~/Views/Desk/Checkout.cshtml", model);
    }

    [HttpPost("checkout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(CheckoutSubmission submission)
    {
        var model = await BuildCheckoutAsync(submission.StudentTag, submission.BookTag, submission.LoanDays);

        if (model.Student is null || model.Copy is null)
        {
            model.ResultMessage = "Scan both a student card and a book before issuing.";
            model.Succeeded = false;
            return View("~/Views/Desk/Checkout.cshtml", model);
        }

        var result = await _circulation.IssueBookAsync(new IssueRequest(
            StudentId: model.Student.Id,
            BookCopyId: model.Copy.Id,
            RequestedLoanDays: submission.LoanDays,
            Method: CirculationMethod.Rfid,
            OperatorUserId: User.Identity?.Name));

        model.Succeeded = result.Succeeded;
        model.ResultMessage = result.Summary;
        model.TransactionNumber = result.TransactionNumber;
        model.DueUtc = result.DueUtc;

        if (result.Succeeded)
        {
            // Clear the panels so the desk is ready for the next student.
            model.Student = null;
            model.Copy = null;
        }

        return View("~/Views/Desk/Checkout.cshtml", model);
    }

    private async Task<CheckoutViewModel> BuildCheckoutAsync(string? studentTag, string? bookTag, int? loanDays)
    {
        var policy = await _policies.GetLoanPolicyAsync();

        var model = new CheckoutViewModel
        {
            StudentTag = studentTag,
            BookTag = bookTag,
            LoanDays = loanDays ?? policy.DefaultLoanDays,
            MaximumLoanDays = policy.MaximumLoanDays,
            Currency = policy.Currency
        };

        if (!string.IsNullOrWhiteSpace(studentTag))
        {
            model.Student = await ResolveStudentAsync(studentTag);
            if (model.Student is null)
            {
                model.StudentTagError = "That card is not registered. Assign it to a student, or use manual issue.";
            }
        }

        if (!string.IsNullOrWhiteSpace(bookTag))
        {
            model.Copy = await ResolveCopyAsync(bookTag);
            if (model.Copy is null)
            {
                model.BookTagError = "That tag is not in the catalogue. Tag the book, or use manual issue.";
            }
        }

        // Show the refusals before the librarian presses Issue, not after.
        if (model.Student is not null && model.Copy is not null)
        {
            var eligibility = await _circulation.ValidateIssueAsync(
                model.Student.Id, model.Copy.Id, model.LoanDays);

            model.IsEligible = eligibility.IsEligible;
            model.EligibilityMessages = eligibility.Refusals.Select(r => r.Message).ToList();
        }

        return model;
    }

    /// <summary>
    /// Resolves a scanned EPC, or a typed roll number as the manual fallback (section 99).
    /// Only ACTIVE tag assignments match — a replaced card must not identify anyone.
    /// </summary>
    private async Task<Student?> ResolveStudentAsync(string tagOrRoll)
    {
        var value = tagOrRoll.Trim();

        // Query the entity directly rather than projecting through the tag, so Department can be
        // eager-loaded for the desk panel.
        return await _db.Students
            .AsNoTracking()
            .Include(s => s.Department)
            .FirstOrDefaultAsync(s =>
                s.RfidTags.Any(t => t.IsActive && t.Epc == value)
                || s.RollNumber == value
                || s.StudentIdNumber == value);
    }

    private async Task<BookCopy?> ResolveCopyAsync(string tagOrCode)
    {
        var value = tagOrCode.Trim();

        // The location chain is eager-loaded because the desk panel shows the most precise
        // location available (specification section 9).
        return await _db.BookCopies
            .AsNoTracking()
            .Include(c => c.Book)
            .Include(c => c.LibrarySection)
            .Include(c => c.Shelf)
            .Include(c => c.ShelfPosition)
            .FirstOrDefaultAsync(c =>
                c.RfidTags.Any(t => t.IsActive && t.Epc == value)
                || c.AccessionNumber == value);
    }

    // ------------------------------------------------------------------ return

    [HttpGet("return")]
    public async Task<IActionResult> Return(string? bookTag)
    {
        return View("~/Views/Desk/Return.cshtml", await BuildReturnAsync(bookTag));
    }

    [HttpPost("return")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnConfirm(string? bookTag)
    {
        var model = await BuildReturnAsync(bookTag);

        if (model.Copy is null)
        {
            model.Succeeded = false;
            model.ResultMessage = "Scan a book before confirming a return.";
            return View("~/Views/Desk/Return.cshtml", model);
        }

        // Book tag alone is enough (section 19); the loan identifies the borrower.
        var result = await _circulation.ReturnBookAsync(new ReturnRequest(
            BookCopyId: model.Copy.Id,
            StudentId: null,
            Method: CirculationMethod.Rfid,
            OperatorUserId: User.Identity?.Name));

        model.Succeeded = result.Succeeded;
        model.ResultMessage = result.Summary;

        if (result.Succeeded)
        {
            model.Copy = null;
            model.Borrower = null;
        }

        return View("~/Views/Desk/Return.cshtml", model);
    }

    private async Task<ReturnViewModel> BuildReturnAsync(string? bookTag)
    {
        var policy = await _policies.GetLoanPolicyAsync();
        var model = new ReturnViewModel { BookTag = bookTag, Currency = policy.Currency };

        if (string.IsNullOrWhiteSpace(bookTag))
        {
            return model;
        }

        model.Copy = await ResolveCopyAsync(bookTag);
        if (model.Copy is null)
        {
            model.LookupError = "That tag is not in the catalogue.";
            return model;
        }

        var loan = await _db.BorrowingRecords
            .AsNoTracking()
            .Include(r => r.Student)
            .FirstOrDefaultAsync(r => r.BookCopyId == model.Copy.Id && r.ReturnDate == null);

        if (loan is null)
        {
            model.LookupError = "This copy is not currently on loan, so there is nothing to return.";
            model.Copy = null;
            return model;
        }

        model.Borrower = loan.Student;
        model.DueDate = loan.DueDate;
        model.TransactionNumber = loan.TransactionNumber;

        // Show the fine before the librarian commits, never as a surprise afterwards.
        var (overdueDays, fine) = await _circulation.CalculateFineAsync(loan.DueDate, DateTime.UtcNow);
        model.ProjectedOverdueDays = overdueDays;
        model.ProjectedFine = fine;

        return model;
    }

    // ------------------------------------------------------------------ manual fallback

    [HttpGet("manual")]
    public async Task<IActionResult> Manual(string? studentQuery, string? bookQuery, int? studentId, int? copyId)
    {
        return View("~/Views/Desk/Manual.cshtml",
            await BuildManualAsync(studentQuery, bookQuery, studentId, copyId, null));
    }

    [HttpPost("manual")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManualIssue(
        string? studentQuery, string? bookQuery, int? studentId, int? copyId, int? loanDays)
    {
        var model = await BuildManualAsync(studentQuery, bookQuery, studentId, copyId, loanDays);

        if (!model.BothSelected)
        {
            model.Succeeded = false;
            model.ResultMessage = "Select both a student and a book copy before issuing.";
            return View("~/Views/Desk/Manual.cshtml", model);
        }

        // Method is the ONLY difference from the RFID path - the rules are identical
        // because this is the same service (specification sections 20, 71, 87).
        var result = await _circulation.IssueBookAsync(new IssueRequest(
            StudentId: model.SelectedStudent!.Id,
            BookCopyId: model.SelectedCopy!.Id,
            RequestedLoanDays: loanDays,
            Method: CirculationMethod.Manual,
            OperatorUserId: User.Identity?.Name));

        model.Succeeded = result.Succeeded;
        model.ResultMessage = result.Summary;
        model.TransactionNumber = result.TransactionNumber;

        if (result.Succeeded)
        {
            model.SelectedStudent = null;
            model.SelectedCopy = null;
            model.SelectedStudentId = null;
            model.SelectedCopyId = null;
        }

        return View("~/Views/Desk/Manual.cshtml", model);
    }

    private async Task<ManualIssueViewModel> BuildManualAsync(
        string? studentQuery, string? bookQuery, int? studentId, int? copyId, int? loanDays)
    {
        var policy = await _policies.GetLoanPolicyAsync();

        var model = new ManualIssueViewModel
        {
            StudentQuery = studentQuery,
            BookQuery = bookQuery,
            SelectedStudentId = studentId,
            SelectedCopyId = copyId,
            LoanDays = loanDays ?? policy.DefaultLoanDays,
            MaximumLoanDays = policy.MaximumLoanDays
        };

        if (!string.IsNullOrWhiteSpace(studentQuery))
        {
            var q = studentQuery.Trim();
            model.StudentMatches = await _db.Students
                .AsNoTracking()
                .Include(s => s.Department)
                .Where(s => s.FullName.Contains(q) || s.RollNumber.Contains(q) || s.StudentIdNumber.Contains(q))
                .OrderBy(s => s.FullName)
                .Take(10)
                .ToListAsync();
        }

        if (!string.IsNullOrWhiteSpace(bookQuery))
        {
            var q = bookQuery.Trim();
            model.CopyMatches = await _db.BookCopies
                .AsNoTracking()
                .Include(c => c.Book)
                .Where(c => c.CopyNumber != "LEGACY"
                            && (c.Book!.Title.Contains(q)
                                || c.Book.Isbn!.Contains(q)
                                || c.AccessionNumber!.Contains(q)))
                .OrderBy(c => c.Book!.Title).ThenBy(c => c.CopyNumber)
                .Take(15)
                .ToListAsync();
        }

        if (studentId is { } sid)
        {
            model.SelectedStudent = await _db.Students.AsNoTracking()
                .Include(s => s.Department)
                .FirstOrDefaultAsync(s => s.Id == sid);
        }

        if (copyId is { } cid)
        {
            model.SelectedCopy = await _db.BookCopies.AsNoTracking()
                .Include(c => c.Book)
                .FirstOrDefaultAsync(c => c.Id == cid);
        }

        if (model.BothSelected)
        {
            var eligibility = await _circulation.ValidateIssueAsync(
                model.SelectedStudent!.Id, model.SelectedCopy!.Id, model.LoanDays);

            model.IsEligible = eligibility.IsEligible;
            model.EligibilityMessages = eligibility.Refusals.Select(r => r.Message).ToList();
        }

        return model;
    }

    [HttpGet("readers")]
    public async Task<IActionResult> Readers()
    {
        var readers = await _db.RfidReaders.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        return View("~/Views/Desk/Readers.cshtml", readers);
    }

    /// <summary>
    /// Switches a reader between acting as a checkout pad and acting as an exit gate.
    ///
    /// Exists because the two roles are mutually exclusive and a library testing with a single reader
    /// has to be able to move it: an un-issued book on a checkout pad is a normal borrow in progress,
    /// while the same book at a gate is a theft. Silencing on the way out matters — a reader that
    /// stops being a gate must not be left beeping.
    /// </summary>
    [HttpPost("readers/purpose/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetReaderPurpose(
        int id,
        RfidReaderPurpose purpose,
        [FromServices] Rfid.Hosting.RfidBeeperAlarm alarm)
    {
        var reader = await _db.RfidReaders.FirstOrDefaultAsync(r => r.Id == id);

        if (reader is null)
        {
            TempData["ReaderTestMessage"] = "That reader no longer exists.";
            TempData["ReaderTestOk"] = false;
            return RedirectToAction(nameof(Readers));
        }

        reader.Purpose = purpose;
        await _db.SaveChangesAsync();

        if (purpose != RfidReaderPurpose.SecurityGate)
        {
            await alarm.SilenceAsync(id);
        }

        TempData["ReaderTestMessage"] = purpose == RfidReaderPurpose.SecurityGate
            ? $"{reader.Name} is now an exit gate. A book read here without an open loan will sound "
              + "the buzzer. It will no longer serve the self-checkout kiosk."
            : $"{reader.Name} is now a {purpose} reader. Gate alarms are off for it.";

        TempData["ReaderTestOk"] = true;
        return RedirectToAction(nameof(Readers));
    }

    /// <summary>
    /// One-shot reachability test.
    ///
    /// Worth having as a button because "the reader is offline" has several very different causes —
    /// wrong IP, unplugged cable, powered off, or the application already holding the D2184's single
    /// TCP connection — and the health columns alone cannot tell a librarian which.
    /// </summary>
    [HttpPost("readers/test/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestReader(
        int id, [FromServices] IRfidConnectionProbe probe, [FromServices] IOptions<RfidOptions> options)
    {
        var reader = await _db.RfidReaders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);

        if (reader is null)
        {
            TempData["ReaderTestMessage"] = "That reader no longer exists.";
            TempData["ReaderTestOk"] = false;
            return RedirectToAction(nameof(Readers));
        }

        if (reader.Transport != RfidTransport.Tcp)
        {
            TempData["ReaderTestMessage"] =
                $"{reader.Name} is not a network reader ({reader.Transport}), so there is no address to test.";
            TempData["ReaderTestOk"] = false;
            return RedirectToAction(nameof(Readers));
        }

        var host = string.IsNullOrWhiteSpace(reader.Host) ? options.Value.Host : reader.Host!;
        var port = reader.Port ?? options.Value.Port;

        var result = await probe.ProbeAsync(host, port, options.Value.ReaderAddressByte);

        // The D2184 serves one TCP client at a time. When the application is already connected, a
        // probe gets its socket accepted but no reply, because the reader is busy streaming
        // inventory to the live connection. That is a healthy reader, and reporting it as "not a
        // D2184" would send a librarian looking for a fault that does not exist.
        var busyWithUs = result.Reachable
                         && !result.SpokeProtocol
                         && reader.Status == RfidReaderStatus.Online;

        TempData["ReaderTestMessage"] = busyWithUs
            ? $"{reader.Name} is reachable at {host}:{port} and is currently connected to this "
              + "application, which is why it did not answer a second connection. Nothing is wrong."
            : $"{reader.Name}: {result.Message}";

        TempData["ReaderTestOk"] = result.SpokeProtocol || busyWithUs;

        return RedirectToAction(nameof(Readers));
    }
}
