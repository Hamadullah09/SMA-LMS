using Library_Management_system.Application.Rfid;
using Library_Management_system.Data;
using Library_Management_system.Models.Desk;
using Library_Management_system.Rfid.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Controllers.Desk;

/// <summary>RFID tag assignment (specification sections 36, 37, 4F).</summary>
[Authorize(Roles = "Admin,Librarian")]
[Route("desk/tags")]
public class TagAssignmentController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IRfidTagService _tags;
    private readonly IRfidLiveFeed _feed;

    public TagAssignmentController(
        ApplicationDbContext db, IRfidTagService tags, IRfidLiveFeed feed)
    {
        _db = db;
        _tags = tags;
        _feed = feed;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? studentQuery, string? bookQuery, string? epc)
    {
        var model = await BuildAsync(studentQuery, bookQuery, epc);

        // Start the live capture from wherever the feed is now, so opening the page does not
        // immediately pick up a tag that was presented to the reader five minutes ago.
        model.ScanCursor = _feed.CurrentSequence;

        return View("~/Views/Desk/Tags.cshtml", model);
    }

    /// <summary>
    /// Live tag capture.
    ///
    /// A tag that has never been assigned has no holder to search for, so the only way to learn its
    /// EPC is to present it to a reader and read it back. Before this, a librarian enrolling a new
    /// student card had to copy 24 hex digits off the tag by hand — which is both slow and the
    /// easiest possible thing to get wrong by one character.
    /// </summary>
    [HttpGet("feed")]
    public async Task<IActionResult> Feed(long cursor)
    {
        var scans = _feed.Since(cursor);

        if (scans.Count == 0)
        {
            return Json(new { cursor, epc = (string?)null });
        }

        // The most recent presentation wins: the librarian is holding one tag against the antenna,
        // and if several were read it is the last one they meant.
        var latest = scans[^1];
        var lookup = await _tags.LookupAsync(latest.Epc);

        return Json(new
        {
            cursor = latest.Sequence,
            epc = latest.Epc,
            known = lookup.IsKnown,
            holder = lookup.HolderDescription
        });
    }

    [HttpPost("student")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignStudent(int studentId, string epc, string? studentQuery)
    {
        var result = await _tags.AssignStudentCardAsync(studentId, epc, User.Identity?.Name);

        var model = await BuildAsync(studentQuery, null, result.Succeeded ? null : epc);
        model.Succeeded = result.Succeeded;
        model.ResultMessage = result.Message;
        model.ConflictHolder = result.PreviousHolder;

        return View("~/Views/Desk/Tags.cshtml", model);
    }

    [HttpPost("book")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignBook(int copyId, string epc, string? bookQuery)
    {
        var result = await _tags.AssignBookTagAsync(copyId, epc, User.Identity?.Name);

        var model = await BuildAsync(null, bookQuery, result.Succeeded ? null : epc);
        model.Succeeded = result.Succeeded;
        model.ResultMessage = result.Message;
        model.ConflictHolder = result.PreviousHolder;

        return View("~/Views/Desk/Tags.cshtml", model);
    }

    [HttpPost("revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int studentId, string reason, string? studentQuery)
    {
        var result = await _tags.RevokeStudentCardAsync(studentId, reason, User.Identity?.Name);

        var model = await BuildAsync(studentQuery, null, null);
        model.Succeeded = result.Succeeded;
        model.ResultMessage = result.Message;

        return View("~/Views/Desk/Tags.cshtml", model);
    }

    private async Task<TagAssignmentViewModel> BuildAsync(string? studentQuery, string? bookQuery, string? epc)
    {
        var model = new TagAssignmentViewModel
        {
            StudentQuery = studentQuery,
            BookQuery = bookQuery,
            ScannedEpc = epc
        };

        // Warn before assigning, not after (specification section 4F).
        if (!string.IsNullOrWhiteSpace(epc))
        {
            var lookup = await _tags.LookupAsync(epc);
            model.ScannedTagIsKnown = lookup.IsKnown;
            model.ScannedTagHolder = lookup.HolderDescription;
        }

        if (!string.IsNullOrWhiteSpace(studentQuery))
        {
            var q = studentQuery.Trim();
            model.Students = await _db.Students
                .AsNoTracking()
                .Include(s => s.RfidTags)
                .Where(s => s.FullName.Contains(q) || s.RollNumber.Contains(q) || s.StudentIdNumber.Contains(q))
                .OrderBy(s => s.FullName)
                .Take(10)
                .ToListAsync();
        }

        if (!string.IsNullOrWhiteSpace(bookQuery))
        {
            var q = bookQuery.Trim();
            model.Copies = await _db.BookCopies
                .AsNoTracking()
                .Include(c => c.Book)
                .Include(c => c.RfidTags)
                .Where(c => c.CopyNumber != "LEGACY"
                            && (c.Book!.Title.Contains(q) || c.AccessionNumber!.Contains(q)))
                .OrderBy(c => c.Book!.Title).ThenBy(c => c.CopyNumber)
                .Take(15)
                .ToListAsync();
        }

        return model;
    }
}
