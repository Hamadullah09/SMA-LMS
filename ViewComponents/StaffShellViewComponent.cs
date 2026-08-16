using Library_Management_system.Data;
using Library_Management_system.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.ViewComponents;

/// <summary>
/// Everything the staff shell needs to draw itself: who is signed in, and the counts behind the
/// topbar badges.
/// </summary>
public sealed class StaffShellModel
{
    public string DisplayName { get; init; } = "Staff";
    public string Initials { get; init; } = "S";
    public string RoleText { get; init; } = "Staff";
    public string ProfileImageUrl { get; init; } = string.Empty;

    public bool IsAdmin { get; init; }
    public bool IsLibrarian { get; init; }

    public int NewUsersCount { get; init; }
    public int NewBooksCount { get; init; }
    public int PendingReservationsCount { get; init; }
    public int UnreadContactCount { get; init; }

    public IReadOnlyList<ContactMessage> RecentContacts { get; init; } = [];

    /// <summary>Everything except contact messages, which carry their own badge.</summary>
    public int BaseNotificationCount => NewUsersCount + NewBooksCount + PendingReservationsCount;

    public int NotificationCount => BaseNotificationCount + UnreadContactCount;
}

/// <summary>
/// Supplies <see cref="StaffShellModel"/> to the staff layout.
/// </summary>
/// <remarks>
/// This exists because the previous admin layout injected <c>ApplicationDbContext</c> directly and
/// ran six queries inline in the .cshtml. That put data access in the view — which the brief
/// explicitly rules out — and made the cost invisible: every admin page paid for it, and nothing
/// in the controller layer showed that it was happening.
/// </remarks>
public sealed class StaffShellViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public StaffShellViewComponent(ApplicationDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var principal = UserClaimsPrincipal;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return View(new StaffShellModel());
        }

        var user = await _users.GetUserAsync(principal);
        var displayName = user?.FullName ?? user?.UserName ?? "Staff";
        var isAdmin = principal.IsInRole("Admin");
        var isLibrarian = principal.IsInRole("Librarian");

        var profileImageUrl = string.Empty;
        if (user is not null)
        {
            profileImageUrl = await _db.UserClaims
                .AsNoTracking()
                .Where(x => x.UserId == user.Id && x.ClaimType == "ProfileImageUrl")
                .OrderByDescending(x => x.Id)
                .Select(x => x.ClaimValue)
                .FirstOrDefaultAsync() ?? string.Empty;
        }

        var since = DateTime.UtcNow.AddDays(-7);

        var model = new StaffShellModel
        {
            DisplayName = displayName,
            Initials = BuildInitials(displayName),
            RoleText = isAdmin ? "Administrator" : isLibrarian ? "Librarian" : "Staff",
            ProfileImageUrl = profileImageUrl,
            IsAdmin = isAdmin,
            IsLibrarian = isLibrarian,

            NewUsersCount = await _db.Users.AsNoTracking()
                .CountAsync(x => (x.CreatedDate ?? DateTime.MinValue) >= since),
            NewBooksCount = await _db.Books.AsNoTracking()
                .CountAsync(x => (x.CreatedDate ?? DateTime.MinValue) >= since),
            PendingReservationsCount = await _db.CartItems.AsNoTracking()
                .CountAsync(x => x.ReservationStatus == "pending"),
            UnreadContactCount = await _db.ContactMessages.AsNoTracking()
                .CountAsync(x => !x.IsRead),

            RecentContacts = await _db.ContactMessages.AsNoTracking()
                .OrderByDescending(x => x.CreatedDate)
                .Take(5)
                .ToListAsync()
        };

        return View(model);
    }

    private static string BuildInitials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            >= 2 => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
            1 => parts[0][0].ToString().ToUpperInvariant(),
            _ => "S"
        };
    }
}
