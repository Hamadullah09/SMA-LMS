using Library_Management_system.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>One account to re-key: found by its current address, moved to a new one.</summary>
public sealed class CredentialResetAccount
{
    public string CurrentEmail { get; set; } = string.Empty;
    public string NewEmail { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Optional display name. Left alone when blank.</summary>
    public string? FullName { get; set; }
}

public sealed class CredentialResetOptions
{
    public const string SectionName = "CredentialReset";

    public bool Enabled { get; set; }
    public List<CredentialResetAccount> Accounts { get; set; } = [];

    /// <summary>
    /// Usernames to delete outright, by username rather than email because a stale account may
    /// share an address with the one that replaced it.
    /// </summary>
    /// <remarks>
    /// Deleting through UserManager rather than the tables directly: it clears the role, claim,
    /// login and token rows that hang off the account, which a bare row delete would strand.
    /// </remarks>
    public List<string> RemoveUserNames { get; set; } = [];
}

/// <summary>
/// Re-keys existing accounts to new addresses and passwords, in place.
/// </summary>
/// <remarks>
/// The accounts themselves are kept rather than recreated, so everything hanging off the user id —
/// the Student row, its RFID cards, borrowing history — survives untouched. Creating fresh users
/// would orphan all of it.
///
/// Passwords live in configuration, never in this file: appsettings.json is gitignored and this
/// source is committed to a public repository. The seeder reads whatever it is given and holds no
/// credentials of its own.
///
/// Identity stores a normalised copy of the email and username for lookups, so both are set through
/// UserManager rather than by assigning the properties directly — a direct assignment leaves
/// NormalizedEmail stale and the account simply stops being findable at sign-in.
/// </remarks>
public static class CredentialResetSeeder
{
    public sealed record Result(string CurrentEmail, string NewEmail, bool Succeeded, string? Error);

    public static async Task<List<Result>> RunAsync(
        UserManager<ApplicationUser> users,
        ApplicationDbContext db,
        CredentialResetOptions options,
        CancellationToken ct = default)
    {
        var results = new List<Result>();

        foreach (var account in options.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.CurrentEmail) ||
                string.IsNullOrWhiteSpace(account.NewEmail) ||
                string.IsNullOrWhiteSpace(account.Password))
            {
                results.Add(new Result(account.CurrentEmail, account.NewEmail, false, "Incomplete entry."));
                continue;
            }

            // Already re-keyed by an earlier run: look it up under the new address so repeating the
            // operation is harmless.
            var user = await users.FindByEmailAsync(account.CurrentEmail)
                       ?? await users.FindByEmailAsync(account.NewEmail);

            if (user is null)
            {
                results.Add(new Result(account.CurrentEmail, account.NewEmail, false, "No such account."));
                continue;
            }

            var error = await ApplyAsync(users, user, account);

            if (error is null)
            {
                await SyncStudentRecordAsync(db, user, account.NewEmail, ct);
            }

            results.Add(new Result(account.CurrentEmail, account.NewEmail, error is null, error));
        }

        foreach (var userName in options.RemoveUserNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var stale = await users.FindByNameAsync(userName);

            if (stale is null)
            {
                results.Add(new Result(userName, "(removed)", true, "Already absent."));
                continue;
            }

            var deleted = await users.DeleteAsync(stale);
            results.Add(new Result(userName, "(removed)", deleted.Succeeded, Describe(deleted)));
        }

        return results;
    }

    private static async Task<string?> ApplyAsync(
        UserManager<ApplicationUser> users,
        ApplicationUser user,
        CredentialResetAccount account)
    {
        var setEmail = await users.SetEmailAsync(user, account.NewEmail);
        if (!setEmail.Succeeded)
        {
            return Describe(setEmail);
        }

        // The username is what Identity signs in against. Left alone it would still be the old
        // address, so the new one would not work.
        var setName = await users.SetUserNameAsync(user, account.NewEmail);
        if (!setName.Succeeded)
        {
            return Describe(setName);
        }

        // Removing the old hash and adding a new one avoids needing the previous password.
        var removed = await users.RemovePasswordAsync(user);
        if (!removed.Succeeded)
        {
            return Describe(removed);
        }

        var added = await users.AddPasswordAsync(user, account.Password);
        if (!added.Succeeded)
        {
            return Describe(added);
        }

        if (!string.IsNullOrWhiteSpace(account.FullName))
        {
            user.FullName = account.FullName;
        }

        // Sign-in requires a confirmed account, and a changed address is unconfirmed by default —
        // which would lock the user out of the account we just handed them.
        user.EmailConfirmed = true;
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);

        var updated = await users.UpdateAsync(user);
        return updated.Succeeded ? null : Describe(updated);
    }

    /// <summary>
    /// Keeps the Student row's address in step with the login.
    /// </summary>
    /// <remarks>
    /// Borrowing history and the staff dossier both match loans on the student's email as well as
    /// the id, because older rows predate the id link. Leaving the old address here would quietly
    /// drop those loans off both screens.
    /// </remarks>
    private static async Task SyncStudentRecordAsync(
        ApplicationDbContext db, ApplicationUser user, string newEmail, CancellationToken ct)
    {
        var student = await db.Students.FirstOrDefaultAsync(s => s.ApplicationUserId == user.Id, ct);

        if (student is not null)
        {
            student.Email = newEmail;
            student.UpdatedUtc = DateTime.UtcNow;

            // Saved per account rather than once at the end. UserManager persists its own changes
            // as it goes, so a trailing blanket save adds nothing for the accounts that worked and
            // runs against a context still tracking any account that failed mid-list.
            await db.SaveChangesAsync(ct);
        }
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
