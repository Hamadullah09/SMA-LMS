using System.Text;
using Library_Management_system.Application.Reporting;
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

    public ReportsController(IReportingService reports, IGlobalSearchService search)
    {
        _reports = reports;
        _search = search;
    }

    [HttpGet("/desk/search")]
    public async Task<IActionResult> Search(string? q)
    {
        var results = string.IsNullOrWhiteSpace(q)
            ? new GlobalSearchResults(string.Empty, [], [], [], [], [])
            : await _search.SearchAsync(q);

        return View("~/Views/Admin/GlobalSearch.cshtml", results);
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
