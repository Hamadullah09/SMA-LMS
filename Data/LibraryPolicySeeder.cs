using Library_Management_system.Application.Policies;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>
/// Writes the default library rules as editable rows (specification sections 22, 58, 62).
///
/// Only inserts keys that are missing, so an administrator's changes are never overwritten by a
/// later deployment.
/// </summary>
public static class LibraryPolicySeeder
{
    private sealed record Definition(string Key, PolicyValueKind Kind, string Category, string Description);

    private static readonly Definition[] Catalogue =
    [
        new(LibraryPolicy.Keys.MaximumLoanDays, PolicyValueKind.Integer, "Loans",
            "Longest loan period a student may choose, in days."),
        new(LibraryPolicy.Keys.DefaultLoanDays, PolicyValueKind.Integer, "Loans",
            "Loan period applied when none is chosen."),
        new(LibraryPolicy.Keys.MaximumBooksPerStudent, PolicyValueKind.Integer, "Loans",
            "How many books a student may have on loan at once."),
        new(LibraryPolicy.Keys.MaximumOverdueBooks, PolicyValueKind.Integer, "Loans",
            "Overdue books tolerated before borrowing is blocked."),
        new(LibraryPolicy.Keys.MaximumRenewals, PolicyValueKind.Integer, "Loans",
            "How many times one loan may be renewed."),
        new(LibraryPolicy.Keys.RenewalDays, PolicyValueKind.Integer, "Loans",
            "Days added by a renewal."),

        new(LibraryPolicy.Keys.FinePerDay, PolicyValueKind.Decimal, "Fines",
            "Charge per day beyond the grace period."),
        new(LibraryPolicy.Keys.FineCurrency, PolicyValueKind.String, "Fines",
            "Currency code shown with fine amounts."),
        new(LibraryPolicy.Keys.FineGracePeriodDays, PolicyValueKind.Integer, "Fines",
            "Days late before a fine begins to accrue."),
        new(LibraryPolicy.Keys.MaximumOutstandingFine, PolicyValueKind.Decimal, "Fines",
            "Unpaid fine total above which borrowing is blocked."),
        new(LibraryPolicy.Keys.LostBookCharge, PolicyValueKind.Decimal, "Fines",
            "Charge applied when a copy is written off as lost."),

        new(LibraryPolicy.Keys.ReservationExpiryDays, PolicyValueKind.Integer, "Reservations",
            "Days a held book waits for collection before the reservation lapses."),
        new(LibraryPolicy.Keys.MaximumReservations, PolicyValueKind.Integer, "Reservations",
            "Active reservations allowed per student."),

        new(LibraryPolicy.Keys.ReminderDaysBeforeDue, PolicyValueKind.String, "Notifications",
            "Comma-separated days before the due date to send reminders."),
        new(LibraryPolicy.Keys.OverdueEscalationDays, PolicyValueKind.String, "Notifications",
            "Comma-separated days after the due date to send overdue warnings."),
        new(LibraryPolicy.Keys.EmailRetryCount, PolicyValueKind.Integer, "Notifications",
            "Delivery attempts before a notification is abandoned."),

        new(LibraryPolicy.Keys.RfidDuplicateWindowMs, PolicyValueKind.Integer, "RFID",
            "Window within which repeated reads of the same tag count as one scan."),
        new(LibraryPolicy.Keys.ReaderHeartbeatIntervalSeconds, PolicyValueKind.Integer, "RFID",
            "How often reader health is polled.")
    ];

    public static async Task SeedAsync(ApplicationDbContext context, CancellationToken ct = default)
    {
        var existing = await context.LibraryPolicies
            .Select(p => p.Key)
            .ToListAsync(ct);

        var present = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var added = false;

        foreach (var definition in Catalogue)
        {
            if (present.Contains(definition.Key))
            {
                continue;
            }

            if (!LibraryPolicyService.Defaults.TryGetValue(definition.Key, out var value))
            {
                continue;
            }

            context.LibraryPolicies.Add(new LibraryPolicy
            {
                Key = definition.Key,
                Value = value,
                ValueKind = definition.Kind,
                Category = definition.Category,
                Description = definition.Description,
                UpdatedBy = "System Seed",
                UpdatedUtc = DateTime.UtcNow
            });

            added = true;
        }

        if (added)
        {
            await context.SaveChangesAsync(ct);
        }
    }
}
