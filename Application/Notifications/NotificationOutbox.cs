using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Notifications;

/// <summary>
/// Queues notifications as database rows inside the caller's transaction
/// (specification sections 51, 53, 87).
///
/// This is what stops email failure from breaking book issuance. The circulation transaction
/// commits with a Notification row alongside it; a background processor sends it afterwards. If
/// SMTP is down, the loan still succeeded and the message is retried.
/// </summary>
public interface INotificationOutbox
{
    /// <summary>
    /// Enqueue without saving - the caller's SaveChanges commits this atomically with whatever
    /// business change prompted it.
    /// </summary>
    void Enqueue(
        NotificationKind kind,
        int studentId,
        string recipient,
        string subject,
        string body,
        string deduplicationKey,
        DateTime? sendAfterUtc = null,
        int? borrowingRecordId = null,
        string? correlationId = null);

    Task<int> DispatchDueAsync(int batchSize, CancellationToken ct = default);
}

public sealed class NotificationOutbox : INotificationOutbox
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailDispatcher _email;
    private readonly ILogger<NotificationOutbox> _logger;

    public NotificationOutbox(
        ApplicationDbContext db,
        IEmailDispatcher email,
        ILogger<NotificationOutbox> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    public void Enqueue(
        NotificationKind kind,
        int studentId,
        string recipient,
        string subject,
        string body,
        string deduplicationKey,
        DateTime? sendAfterUtc = null,
        int? borrowingRecordId = null,
        string? correlationId = null)
    {
        _db.Notifications.Add(new Notification
        {
            Kind = kind,
            Channel = NotificationChannel.Email,
            Status = NotificationStatus.Pending,
            StudentId = studentId,
            Recipient = recipient,
            Subject = subject,
            Body = body,
            DeduplicationKey = deduplicationKey,
            NextAttemptUtc = sendAfterUtc ?? DateTime.UtcNow,
            BorrowingTransactionId = borrowingRecordId,
            CorrelationId = correlationId
        });
    }

    /// <summary>
    /// Send everything now due. Catch-up based, not tick based: it asks "what is overdue to be
    /// sent?" rather than assuming the previous run happened, so an app-pool recycle loses nothing
    /// (DEPLOYMENT.md section 2).
    /// </summary>
    public async Task<int> DispatchDueAsync(int batchSize, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var due = await _db.Notifications
            .Where(n => n.Status == NotificationStatus.Pending && n.NextAttemptUtc <= now)
            .OrderBy(n => n.NextAttemptUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var notification in due)
        {
            notification.AttemptCount++;
            notification.LastAttemptUtc = now;

            if (string.IsNullOrWhiteSpace(notification.Recipient))
            {
                notification.Status = NotificationStatus.Abandoned;
                notification.LastError = "No recipient address on the student record.";
                continue;
            }

            try
            {
                await _email.SendAsync(
                    notification.Recipient,
                    notification.Subject ?? "SMA Library Management System",
                    notification.Body ?? string.Empty,
                    ct);

                notification.Status = NotificationStatus.Sent;
                notification.SentUtc = DateTime.UtcNow;
                notification.LastError = null;
                sent++;
            }
            catch (Exception ex)
            {
                // Never rethrow: one bad address must not stop the batch.
                notification.LastError = ex.Message;

                if (notification.AttemptCount >= MaxAttempts)
                {
                    notification.Status = NotificationStatus.Abandoned;
                    _logger.LogError(ex,
                        "Notification {Id} abandoned after {Attempts} attempts.",
                        notification.Id, notification.AttemptCount);
                }
                else
                {
                    // Exponential backoff, so a dead SMTP server is not hammered.
                    notification.NextAttemptUtc =
                        DateTime.UtcNow.AddMinutes(Math.Pow(2, notification.AttemptCount));

                    _logger.LogWarning(ex,
                        "Notification {Id} failed (attempt {Attempts}); retrying at {NextAttempt}.",
                        notification.Id, notification.AttemptCount, notification.NextAttemptUtc);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return sent;
    }

    private const int MaxAttempts = 5;
}

/// <summary>
/// Thin seam over the inherited MailKit EmailSender, so the outbox can be tested without SMTP.
/// </summary>
public interface IEmailDispatcher
{
    Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken ct = default);
}

public sealed class EmailDispatcher : IEmailDispatcher
{
    private readonly Microsoft.AspNetCore.Identity.UI.Services.IEmailSender _sender;

    public EmailDispatcher(Microsoft.AspNetCore.Identity.UI.Services.IEmailSender sender)
        => _sender = sender;

    public Task SendAsync(string recipient, string subject, string htmlBody, CancellationToken ct = default)
        => _sender.SendEmailAsync(recipient, subject, htmlBody);
}
