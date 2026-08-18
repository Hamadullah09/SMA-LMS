using System.Text;
using Library_Management_system.Application.Reporting;
using Library_Management_system.Application.Policies;
using Library_Management_system.Application.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Library_Management_system.Controllers.Admin;

/// <summary>Reports (§54) and global search (§103). Both are librarian-and-above tools.</summary>
[Authorize(Roles = "Admin,Librarian")]
public class ReportsController : Controller
{
    private readonly IReportingService _reports;
    private readonly IGlobalSearchService _search;
    private readonly IStudentDossierService _dossier;
    private readonly ILibraryPolicyService _policies;

    public ReportsController(
        IReportingService reports,
        IGlobalSearchService search,
        IStudentDossierService dossier,
        ILibraryPolicyService policies)
    {
        _reports = reports;
        _search = search;
        _dossier = dossier;
        _policies = policies;
    }

    [HttpGet("/desk/search")]
    public async Task<IActionResult> Search(string? q)
    {
        var results = string.IsNullOrWhiteSpace(q)
            ? new GlobalSearchResults(string.Empty, [], [], [], [], [])
            : await _search.SearchAsync(q);

        return View("~/Views/Admin/GlobalSearch.cshtml", results);
    }

    /// <summary>
    /// Everything on file for one student (§103). Global search sent a student hit to the
    /// manual-issue screen, which shows a name and nothing else; the librarian then had to open
    /// loans, returns, fines and reservations separately to answer a question at the desk.
    /// </summary>
    [HttpGet("/desk/student/{id:int}")]
    public async Task<IActionResult> Student(int id, CancellationToken ct)
    {
        var dossier = await _dossier.GetAsync(id, ct);

        if (dossier is null)
        {
            // Still a 404 to caches and crawlers, but with something a librarian can act on.
            // A bare NotFound() renders blank here: nothing routes to Error/404.
            ViewBag.StudentId = id;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("~/Views/Admin/StudentNotFound.cshtml");
        }

        ViewBag.Policy = await _policies.GetLoanPolicyAsync(ct);
        return View("~/Views/Admin/StudentDossier.cshtml", dossier);
    }

    [HttpGet("/admin/sma/reports")]
    public async Task<IActionResult> Index(string? report, DateTime? from, DateTime? to)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var key = report ?? _reports.Available[0].Key;

        ViewBag.Definitions = _reports.Available;
        ViewBag.From = fromUtc;
        ViewBag.To = toUtc;

        return View("~/Views/Admin/Reports.cshtml", await _reports.RunAsync(key, fromUtc, toUtc));
    }

    [HttpGet("/admin/sma/reports/export")]
    public async Task<IActionResult> Export(string report, DateTime? from, DateTime? to)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var result = await _reports.RunAsync(report, fromUtc, toUtc);

        var csv = _reports.ToCsv(result);
        var name = $"sma-{result.Key}-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.csv";

        // UTF-8 BOM so Excel opens non-ASCII names correctly rather than as mojibake.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", name);
    }

    /// <summary>Defaults to the last 30 days, and tolerates the dates being the wrong way round.</summary>
    private static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to)
    {
        var toUtc = (to ?? DateTime.UtcNow).Date;
        var fromUtc = (from ?? toUtc.AddDays(-30)).Date;

        return fromUtc > toUtc ? (toUtc, fromUtc) : (fromUtc, toUtc);
    }
}
