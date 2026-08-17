using Library_Management_system.Application.Rfid;
using Library_Management_system.Data;
using Library_Management_system.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Controllers.Admin;

/// <summary>
/// Bulk RFID tag import.
///
/// Admin only, and preview-first by design: the operation creates catalogue rows in bulk from a
/// supplier file, and the file is the one artefact in the process nobody has checked. Seeing what
/// will happen before it happens is the difference between an import and an accident.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/rfid/import")]
public class RfidImportController : Controller
{
    /// <summary>
    /// Cap on an uploaded file. Generous for a tag manifest (200 rows is roughly 8 KB) and small
    /// enough that a mistaken upload cannot be used to exhaust memory.
    /// </summary>
    private const int MaxUploadBytes = 2 * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IRfidTagImportService _import;

    public RfidImportController(ApplicationDbContext db, IRfidTagImportService import)
    {
        _db = db;
        _import = import;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(new RfidImportViewModel()));
    }

    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Preview(
        IFormFile? file, string? csv, bool useBundled, TagImportDistribution distribution)
    {
        var model = new RfidImportViewModel { Distribution = distribution };

        var content = await ResolveCsvAsync(file, csv, useBundled);
        if (content.Error is not null)
        {
            model.ErrorMessage = content.Error;
            return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
        }

        model.Csv = content.Csv;
        model.Report = await _import.ImportAsync(
            content.Csv!, new TagImportOptions(distribution, DryRun: true), User.Identity?.Name);

        return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
    }

    [HttpPost("apply")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Apply(string? csv, bool useBundled, TagImportDistribution distribution)
    {
        var model = new RfidImportViewModel { Distribution = distribution };

        // Apply never accepts a file upload: it works from the text the preview was computed over,
        // so what gets written is what the operator actually saw.
        var content = await ResolveCsvAsync(null, csv, useBundled);
        if (content.Error is not null)
        {
            model.ErrorMessage = content.Error;
            return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
        }

        model.Csv = content.Csv;
        model.Report = await _import.ImportAsync(
            content.Csv!, new TagImportOptions(distribution, DryRun: false), User.Identity?.Name);
        model.Applied = true;

        return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
    }

    private async Task<(string? Csv, string? Error)> ResolveCsvAsync(
        IFormFile? file, string? csv, bool useBundled)
    {
        if (useBundled)
        {
            var bundled = await _import.ReadBundledFileAsync();
            return bundled is null
                ? (null, $"The bundled tag file was not found at {RfidTagImportService.BundledFileRelativePath}.")
                : (bundled, null);
        }

        if (file is { Length: > 0 })
        {
            if (file.Length > MaxUploadBytes)
            {
                return (null, "That file is larger than 2 MB. A tag manifest should be far smaller.");
            }

            using var reader = new StreamReader(file.OpenReadStream());
            return (await reader.ReadToEndAsync(), null);
        }

        if (!string.IsNullOrWhiteSpace(csv))
        {
            return (csv, null);
        }

        return (null, "Choose the bundled file, upload a CSV, or paste rows before continuing.");
    }

    // ------------------------------------------------------------------ student cards

    [HttpPost("cards/preview")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> PreviewCards(IFormFile? cardFile, string? cardText, bool useBundledCards)
    {
        var model = new RfidImportViewModel();

        var content = await ResolveCardTextAsync(cardFile, cardText, useBundledCards);
        if (content.Error is not null)
        {
            model.CardErrorMessage = content.Error;
            return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
        }

        model.CardText = content.Text;
        model.CardReport = await _import.ImportStudentCardsAsync(
            content.Text!, dryRun: true, actor: User.Identity?.Name);

        return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
    }

    [HttpPost("cards/apply")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> ApplyCards(string? cardText, bool useBundledCards)
    {
        var model = new RfidImportViewModel();

        // Works from the text the preview was computed over, so what gets written is what was seen.
        var content = await ResolveCardTextAsync(null, cardText, useBundledCards);
        if (content.Error is not null)
        {
            model.CardErrorMessage = content.Error;
            return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
        }

        model.CardText = content.Text;
        model.CardReport = await _import.ImportStudentCardsAsync(
            content.Text!, dryRun: false, actor: User.Identity?.Name);
        model.CardsApplied = true;

        return View("~/Views/Admin/RfidImport.cshtml", await BuildAsync(model));
    }

    private async Task<(string? Text, string? Error)> ResolveCardTextAsync(
        IFormFile? file, string? text, bool useBundled)
    {
        if (useBundled)
        {
            var bundled = await _import.ReadBundledStudentCardFileAsync();
            return bundled is null
                ? (null, $"The bundled card sheet was not found at "
                         + $"{RfidTagImportService.BundledStudentCardFileRelativePath}.")
                : (bundled, null);
        }

        if (file is { Length: > 0 })
        {
            if (file.Length > MaxUploadBytes)
            {
                return (null, "That file is larger than 2 MB. A card label sheet should be far smaller.");
            }

            using var reader = new StreamReader(file.OpenReadStream());
            return (await reader.ReadToEndAsync(), null);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            return (text, null);
        }

        return (null, "Choose the bundled sheet, upload a file, or paste EPCs before continuing.");
    }

    private async Task<RfidImportViewModel> BuildAsync(RfidImportViewModel model)
    {
        var bundled = await _import.ReadBundledFileAsync();
        model.BundledFileAvailable = bundled is not null;

        if (bundled is not null)
        {
            // Line count less the header, purely so the screen can say how big the file is.
            model.BundledRowCount = Math.Max(
                bundled.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length - 1, 0);
        }

        model.TitleCount = await _db.Books.CountAsync();
        model.CopyCount = await _db.BookCopies.CountAsync(c => c.CopyNumber != "LEGACY");
        model.TaggedCopyCount = await _db.BookCopies
            .CountAsync(c => c.CopyNumber != "LEGACY" && c.RfidTags.Any(t => t.IsActive));

        var cards = await _import.ReadBundledStudentCardFileAsync();
        model.CardFileAvailable = cards is not null;

        if (cards is not null)
        {
            // Less the header, purely so the screen can say how many cards the sheet holds.
            model.CardFileRowCount = Math.Max(
                cards.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length - 1, 0);
        }

        model.StudentCount = await _db.Students.CountAsync();
        model.StudentsWithCardCount = await _db.Students
            .CountAsync(s => s.RfidTags.Any(t => t.IsActive));

        return model;
    }
}
