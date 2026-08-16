using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Notifications;

/// <summary>
/// Due reminders, overdue escalation and outbox delivery (specification sections 24, 52, 53).
///
/// Designed for shared Windows hosting, where the app pool recycles without warning
/// (DEPLOYMENT.md section 2):
///
///   * every pass is CATCH-UP based - it asks what is currently due, never assuming the previous
///     tick ran
///   * every notification carries a deduplication key unique per (student, loan, kind, date), so
///     a pass that repeats after a recycle cannot send a second copy
///   * no circulation transaction depends on this service; it only sends messages
/// </summary>
public sealed class OverdueBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int DispatchBatchSize = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OverdueBackgroundService> _logger;

    public OverdueBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<OverdueBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the application finish starting before doing database work.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed pass must never kill the service; the next pass catches up.
                _logger.LogError(ex, "Overdue/notification pass failed; will retry.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var policies = scope.ServiceProvider.GetRequiredService<ILibraryPolicyService>();
        var outbox = scope.ServiceProvider.GetRequiredService<INotificationOutbox>();

        await QueueDueRemindersAsync(db, policies, outbox, ct);
        await QueueOverdueWarningsAsync(db, policies, outbox, ct);
        await db.SaveChangesAsync(ct);

        var sent = await outbox.DispatchDueAsync(DispatchBatchSize, ct);
        if (sent > 0)
        {
            _logger.LogInformation("Dispatched {Count} notification(s).", sent);
        }
    }

    private static async Task QueueDueRemindersAsync(
        ApplicationDbContext db, ILibraryPolicyService policies, INotificationOutbox outbox, CancellationToken ct)
    {
        var offsets = ParseDayList(
            await policies.GetStringAsync(LibraryPolicy.Keys.ReminderDaysBeforeDue, "3,1", ct));

        var today = DateTime.UtcNow.Date;

        foreach (var daysAhead in offsets)
        {
            var target = today.AddDays(daysAhead);

            var loans = await LoansDueOn(db, target).ToListAsync(ct);

            foreach (var loan in loans)
            {
                outbox.Enqueue(
                    kind: NotificationKind.DueReminder,
                    studentId: loan.StudentId!.Value,
                    recipient: loan.Email ?? string.Empty,
                    subject: $"Library book due in {daysAhead} day(s)",
                    body: BuildReminderBody(loan, daysAhead),
                    deduplicationKey: $"due-{daysAhead}:{loan.LoanId}",
                    borrowingRecordId: loan.LoanId);
            }
        }
    }

    private static async Task QueueOverdueWarningsAsync(
        ApplicationDbContext db, ILibraryPolicyService policies, INotificationOutbox outbox, CancellationToken ct)
    {
        var offsets = ParseDayList(
            await policies.GetStringAsync(LibraryPolicy.Keys.OverdueEscalationDays, "1,3,7,14", ct));

        var currency = await policies.GetStringAsync(LibraryPolicy.Keys.FineCurrency, "PKR", ct);
        var rate = await policies.GetDecimalAsync(LibraryPolicy.Keys.FinePerDay, 20m, ct);
        var today = DateTime.UtcNow.Date;

        foreach (var daysLate in offsets)
        {
            var target = today.AddDays(-daysLate);

            var loans = await LoansDueOn(db, target).ToListAsync(ct);

            foreach (var loan in loans)
            {
                outbox.Enqueue(
                    kind: NotificationKind.OverdueWarning,
                    studentId: loan.StudentId!.Value,
                    recipient: loan.Email ?? string.Empty,
                    subject: $"Library book {daysLate} day(s) overdue",
                    body: BuildOverdueBody(loan, daysLate, currency, rate * daysLate),
                    deduplicationKey: $"overdue-{daysLate}:{loan.LoanId}",
                    borrowingRecordId: loan.LoanId);
            }
        }
    }

    private sealed record LoanNotice(int LoanId, int? StudentId, string? Email, string Title, DateTime DueDate, string? TransactionNumber);

    private static IQueryable<LoanNotice> LoansDueOn(ApplicationDbContext db, DateTime dueDate) =>
        db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.ReturnDate == null
                        && r.StudentId != null
                        && r.DueDate.Date == dueDate.Date)
            .Select(r => new LoanNotice(
                r.Id,
                r.StudentId,
                r.Student!.Email,
                r.Book!.Title,
                r.DueDate,
                r.TransactionNumber));

    private static string BuildReminderBody(LoanNotice loan, int daysAhead) =>
        $"""
         <p>This is a reminder from the SMA Library Management System.</p>
         <p><strong>{loan.Title}</strong> is due in {daysAhead} day(s), on {loan.DueDate:dd MMMM yyyy}.</p>
         <p>Transaction: {loan.TransactionNumber}</p>
         <p>Please return or renew it by the due date to avoid a fine.</p>
         """;

    private static string BuildOverdueBody(LoanNotice loan, int daysLate, string currency, decimal fine) =>
        $"""
         <p>This is a notice from the SMA Library Management System.</p>
         <p><strong>{loan.Title}</strong> was due on {loan.DueDate:dd MMMM yyyy} and is now
         {daysLate} day(s) overdue.</p>
         <p>Transaction: {loan.TransactionNumber}</p>
         <p>The fine currently stands at approximately {currency} {fine:0.00} and continues to grow
         each day until the book is returned.</p>
         """;

    /// <summary>Parses "3,1" or "1,3,7,14" into distinct positive day offsets.</summary>
    internal static IReadOnlyList<int> ParseDayList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var value) ? value : -1)
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
}
