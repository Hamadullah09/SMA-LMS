using System.Security.Claims;
using Library_Management_system.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>
/// Gives student records a working login.
///
/// A <c>Student</c> and an <c>ApplicationUser</c> are separate identities on purpose — a student
/// exists in the registry before they ever sign in (specification section 35) — so a seeded student
/// has no account and cannot reach the portal, the cart, or the cart-to-kiosk handover. This closes
/// that gap for test data.
///
/// Four things have to be true for a seeded account to actually sign in, and missing any one of them
/// produces a login that fails for a different and confusing reason:
///   * a password that satisfies the configured Identity rules
///   * EmailConfirmed, because SignIn.RequireConfirmedAccount is on
///   * no lockout, because registration deliberately locks new accounts
///   * an approval claim, because registration leaves accounts pending an administrator
///
/// Development only. <see cref="Infrastructure.ProductionGuards"/> refuses to start Production with
/// this enabled, for the same reason it refuses the demo users: these are accounts with a password
/// written in configuration.
/// </summary>
public static class StudentAccountSeeder
{
    public sealed record SeededAccount(
        string RollNumber, string FullName, string Email, bool Created);

    public static async Task<List<SeededAccount>> SeedAsync(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        string password,
        CancellationToken ct = default)
    {
        var results = new List<SeededAccount>();

        // Demo Student already has its own account with its own documented password; leaving it alone
        // avoids silently changing a credential that is referenced elsewhere.
        var students = await context.Students
            .Where(s => s.Email != null && s.Email != "student@library.com")
            .OrderBy(s => s.RollNumber)
            .ToListAsync(ct);

        foreach (var student in students)
        {
            var email = student.Email!;
            var user = await userManager.FindByEmailAsync(email);
            var created = false;

            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    FullName = student.FullName,
                    EmailConfirmed = true,
                    CreatedBy = "Student Account Seed",
                    CreatedDate = DateTime.UtcNow
                };

                var create = await userManager.CreateAsync(user, password);

                if (!create.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not create the login for {email}: "
                        + string.Join("; ", create.Errors.Select(e => e.Description)));
                }

                created = true;
            }
            else
            {
                // Re-running must leave a usable account, so the password is reset to the configured
                // one rather than assumed to still match.
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var reset = await userManager.ResetPasswordAsync(user, token, password);

                if (!reset.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not reset the password for {email}: "
                        + string.Join("; ", reset.Errors.Select(e => e.Description)));
                }
            }

            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                await userManager.UpdateAsync(user);
            }

            // Registration locks new accounts pending approval; a seeded one must be ready to use.
            await userManager.SetLockoutEndDateAsync(user, null);

            var claims = await userManager.GetClaimsAsync(user);
            foreach (var stale in claims.Where(c => c.Type == AccountApproval.ClaimType))
            {
                await userManager.RemoveClaimAsync(user, stale);
            }

            await userManager.AddClaimAsync(
                user, new Claim(AccountApproval.ClaimType, AccountApproval.Approved));

            if (!await userManager.IsInRoleAsync(user, "User"))
            {
                await userManager.AddToRoleAsync(user, "User");
            }

            // Without this link the account can sign in but is not a borrower, so the kiosk cannot
            // identify them from their login.
            if (student.ApplicationUserId != user.Id)
            {
                student.ApplicationUserId = user.Id;
                await context.SaveChangesAsync(ct);
            }

            results.Add(new SeededAccount(student.RollNumber, student.FullName, email, created));
        }

        return results;
    }
}
