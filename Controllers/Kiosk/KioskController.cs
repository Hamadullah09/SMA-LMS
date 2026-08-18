using System.Security.Claims;
using Library_Management_system.Application.Kiosk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Controllers.Kiosk;

/// <summary>
/// The unattended self-service station (specification sections 20, 46, 96).
///
/// Anonymous by necessity — a student identifies themselves by tapping a card on the antenna, not by
/// logging in — and thin by design: the state machine is <see cref="IKioskService"/> and every
/// borrowing rule is <c>ICirculationService</c>, the same one the staff desk uses. See
/// <see cref="KioskOptions"/> for what "anonymous" is and is not allowed to mean here.
///
/// The browser holds no state at all. It polls <see cref="State"/> and posts button presses, so a
/// reload, a crash or a second tab cannot disagree with the physical station about whose books are
/// on the pad.
/// </summary>
[AllowAnonymous]
[Route("kiosk")]
public class KioskController : Controller
{
    private readonly IKioskService _kiosk;
    private readonly KioskOptions _options;

    public KioskController(IKioskService kiosk, IOptions<KioskOptions> options)
    {
        _kiosk = kiosk;
        _options = options.Value;
    }

    [HttpGet("")]
    [HttpGet("{readerId:int}")]
    public async Task<IActionResult> Index(int? readerId, KioskMode? mode)
    {
        if (Refuse() is { } refusal)
        {
            return refusal;
        }

        var resolved = readerId ?? await _kiosk.ResolveDefaultReaderIdAsync();

        if (resolved is null)
        {
            // Meaningful empty state with a next action (specification section 104).
            return View("~/Views/Kiosk/Unavailable.cshtml");
        }

        var state = await _kiosk.RefreshAsync(resolved.Value);

        // "Return a book" links here with mode=return so a student lands on the return screen
        // rather than arriving in borrow mode and having to find the toggle.
        //
        // Only from Idle. A station is shared hardware: if somebody is mid-basket, switching mode
        // would clear their books out from under them, because the same copy on the pad means the
        // opposite thing in the other mode.
        if (mode is { } requestedMode && state.Stage == KioskStage.Idle && state.Mode != requestedMode)
        {
            state = await _kiosk.SetModeAsync(resolved.Value, requestedMode);
        }

        // A student arriving from their own cart is already identified, so asking them to tap a card
        // they may not be carrying would stop the flow dead. Only on this initial navigation: the
        // poll must never re-adopt an identity the idle timer has deliberately just cleared.
        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!string.IsNullOrWhiteSpace(userId))
            {
                state = await _kiosk.AdoptSignedInStudentAsync(
                    resolved.Value, userId, User.FindFirstValue(ClaimTypes.Email));
            }
        }

        return View("~/Views/Kiosk/Index.cshtml", state);
    }

    /// <summary>
    /// The poll. Also the tick that drives the station: draining scans and expiring an idle session
    /// both happen here, so the station advances exactly as often as somebody is watching it.
    /// </summary>
    [HttpGet("state/{readerId:int}")]
    public async Task<IActionResult> State(int readerId, CancellationToken ct)
    {
        if (Refuse() is { } refusal)
        {
            return refusal;
        }

        return Json(await _kiosk.RefreshAsync(readerId, ct));
    }

    [HttpPost("mode/{readerId:int}")]
    public async Task<IActionResult> Mode(int readerId, KioskMode mode, CancellationToken ct)
    {
        if (Refuse() is { } refusal)
        {
            return refusal;
        }

        return Json(await _kiosk.SetModeAsync(readerId, mode, ct));
    }

    [HttpPost("remove/{readerId:int}")]
    public async Task<IActionResult> Remove(int readerId, int bookCopyId, CancellationToken ct)
    {
        if (Refuse() is { } refusal)
        {
            return refusal;
        }

        return Json(await _kiosk.RemoveItemAsync(readerId, bookCopyId, ct));
    }

    [HttpPost("reset/{readerId:int}")]
    public async Task<IActionResult> Reset(int readerId, CancellationToken ct)
    {
        if (Refuse() is { } refusal)
        {
            return refusal;
        }

        return Json(await _kiosk.ResetAsync(readerId, ct));
    }

    [HttpPost("commit/{readerId:int}")]
    public async Task<IActionResult> Commit(int readerId, CancellationToken ct)
    {
        if (Refuse() is { } refusal)
        {
            return refusal;
        }

        return Json(await _kiosk.CommitAsync(readerId, ct));
    }

    /// <summary>
    /// Single gate for every action, so a new endpoint cannot be added that quietly skips the check.
    /// </summary>
    private IActionResult? Refuse()
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        return _options.IsClientAllowed(HttpContext.Connection.RemoteIpAddress)
            ? null
            : Forbid();
    }
}
