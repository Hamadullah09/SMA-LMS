using Library_Management_system.Application.Assistant;
using Library_Management_system.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ApplicationUser = Library_Management_system.Models.ApplicationUser;

namespace Library_Management_system.Controllers.Portal;

/// <summary>
/// Library assistant (specification sections 12, 13).
///
/// Browsing questions are open to anyone; account questions resolve the student from the
/// signed-in user and never from anything the caller supplies (§43).
/// </summary>
[Route("assistant")]
public class AssistantController : Controller
{
    private readonly ILibraryAssistant _assistant;
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public AssistantController(
        ILibraryAssistant assistant, ApplicationDbContext db, UserManager<ApplicationUser> users)
    {
        _assistant = assistant;
        _db = db;
        _users = users;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q)
    {
        ViewBag.Question = q;
        ViewBag.Answer = string.IsNullOrWhiteSpace(q) ? null : await AskAsync(q);
        return View("~/Views/Portal/Assistant.cshtml");
    }

    /// <summary>JSON endpoint, so the assistant can be embedded on other pages later.</summary>
    [HttpGet("ask")]
    public async Task<IActionResult> Ask(string q) => Json(await AskAsync(q));

    private async Task<AssistantAnswer> AskAsync(string question)
    {
        int? studentId = null;

        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = _users.GetUserId(User);
            studentId = await _db.Students
                .AsNoTracking()
                .Where(s => s.ApplicationUserId == userId)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync();
        }

        return await _assistant.AskAsync(question, studentId);
    }
}
