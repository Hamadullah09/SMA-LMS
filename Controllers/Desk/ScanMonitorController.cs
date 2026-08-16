using Library_Management_system.Application.Rfid;
using Library_Management_system.Data;
using Library_Management_system.Rfid;
using Library_Management_system.Rfid.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Controllers.Desk;

/// <summary>
/// Live scan monitor (specification section 47) and the simulator control that drives it (§82).
///
/// Presenting a tag here goes through exactly the same pipeline a real D2184 read would:
///   observation -> IRfidScanProcessor (debounce) -> IRfidScanRecorder (persist + resolve)
///
/// That is what makes the simulator worth having: it exercises the real code, not a parallel
/// path. The simulate action is refused outside Development.
/// </summary>
[Authorize(Roles = "Admin,Librarian")]
[Route("desk/scans")]
public class ScanMonitorController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IRfidScanProcessor _processor;
    private readonly IRfidScanRecorder _recorder;
    private readonly RfidOptions _options;
    private readonly IWebHostEnvironment _environment;

    public ScanMonitorController(
        ApplicationDbContext db,
        IRfidScanProcessor processor,
        IRfidScanRecorder recorder,
        IOptions<RfidOptions> options,
        IWebHostEnvironment environment)
    {
        _db = db;
        _processor = processor;
        _recorder = recorder;
        _options = options.Value;
        _environment = environment;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewBag.CanSimulate = _environment.IsDevelopment() && _options.IsSimulator;
        ViewBag.Readers = await _db.RfidReaders.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

        var recent = await _db.RfidScanEvents
            .AsNoTracking()
            .OrderByDescending(e => e.LastObservedUtc)
            .Take(25)
            .Select(e => new Models.Desk.ScanLine
            {
                Epc = e.Epc,
                ReaderName = e.Reader!.Name,
                ObservedUtc = e.LastObservedUtc,
                ReadCount = e.ReadCount,
                Rssi = e.Rssi,
                Antenna = e.Antenna,
                Kind = e.ResolvedKind == null ? "Unknown" : e.ResolvedKind.ToString()!,
                Resolved = e.ResolvedStudentId != null
                    ? _db.Students.Where(s => s.Id == e.ResolvedStudentId).Select(s => s.FullName).FirstOrDefault()
                    : e.ResolvedBookCopyId != null
                        ? _db.BookCopies.Where(c => c.Id == e.ResolvedBookCopyId)
                            .Select(c => c.Book!.Title + " — copy " + c.CopyNumber).FirstOrDefault()
                        : null
            })
            .ToListAsync();

        return View("~/Views/Desk/Scans.cshtml", recent);
    }

    /// <summary>
    /// Present a tag to a reader. Development-only: in production the observations come from
    /// hardware, and an endpoint that fabricates scans would be a way to forge circulation events.
    /// </summary>
    [HttpPost("simulate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Simulate(int readerId, string epc, int repeats = 1)
    {
        if (!_environment.IsDevelopment() || !_options.IsSimulator)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(epc))
        {
            TempData["ScanMessage"] = "Enter a tag EPC to present.";
            return RedirectToAction(nameof(Index));
        }

        var now = DateTime.UtcNow;
        var emitted = 0;
        ScanResolution? last = null;

        // Repeats deliberately go through the debouncer, so the UI demonstrates suppression
        // rather than bypassing it.
        for (var i = 0; i < Math.Clamp(repeats, 1, 50); i++)
        {
            var observation = new RfidObservation(
                readerId, epc.Trim().ToUpperInvariant(), now.AddMilliseconds(i * 20), 70, 1);

            var scan = _processor.Process(observation, TimeSpan.FromMilliseconds(_options.DuplicateWindowMs));
            if (scan is not null)
            {
                last = await _recorder.RecordAsync(scan);
                emitted++;
            }
        }

        TempData["ScanMessage"] = emitted == 0
            ? $"{repeats} reads suppressed as duplicates — the tag is still inside the "
              + $"{_options.DuplicateWindowMs}ms window from a previous scan."
            : $"{repeats} raw read(s) produced {emitted} logical scan(s). Resolved: {last?.Description}.";

        return RedirectToAction(nameof(Index));
    }
}
