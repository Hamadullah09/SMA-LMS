using System.ComponentModel.DataAnnotations;
using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Domain.Entities;

/// <summary>
/// A configurable library rule (specification sections 22, 58). Replaces the hardcoded
/// DefaultBorrowingDays = 14 and FinePerLateDay = 1.00m constants found in
/// ManageBorrowingBookController during the audit.
///
/// Stored as key/value with a declared type so the policy service can parse safely and so new
/// policies need no schema change.
/// </summary>
public class LibraryPolicy
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Value { get; set; } = string.Empty;

    public PolicyValueKind ValueKind { get; set; } = PolicyValueKind.Integer;

    [MaxLength(400)]
    public string? Description { get; set; }

    /// <summary>Grouping for the admin UI, e.g. "Loans", "Fines", "Reservations".</summary>
    [MaxLength(60)]
    public string? Category { get; set; }

    [MaxLength(150)]
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Canonical policy keys. Defaults live in the seeder, not in code paths.</summary>
    public static class Keys
    {
        public const string MaximumLoanDays = "Loans.MaximumLoanDays";
        public const string DefaultLoanDays = "Loans.DefaultLoanDays";
        public const string MaximumBooksPerStudent = "Loans.MaximumBooksPerStudent";
        public const string MaximumOverdueBooks = "Loans.MaximumOverdueBooks";
        public const string MaximumRenewals = "Loans.MaximumRenewals";
        public const string RenewalDays = "Loans.RenewalDays";

        public const string FinePerDay = "Fines.PerDay";
        public const string FineCurrency = "Fines.Currency";
        public const string FineGracePeriodDays = "Fines.GracePeriodDays";
        public const string MaximumOutstandingFine = "Fines.MaximumOutstanding";
        public const string LostBookCharge = "Fines.LostBookCharge";

        public const string ReservationExpiryDays = "Reservations.ExpiryDays";
        public const string MaximumReservations = "Reservations.MaximumPerStudent";

        public const string ReminderDaysBeforeDue = "Notifications.ReminderDaysBeforeDue";
        public const string OverdueEscalationDays = "Notifications.OverdueEscalationDays";
        public const string EmailRetryCount = "Notifications.EmailRetryCount";

        public const string RfidDuplicateWindowMs = "Rfid.DuplicateWindowMs";
        public const string ReaderHeartbeatIntervalSeconds = "Rfid.ReaderHeartbeatIntervalSeconds";
    }
}

/// <summary>
/// Append-only record of every significant operation (specification section 38).
/// No update or delete is mapped for this entity; the intent is that the application login is
/// also denied UPDATE/DELETE on the table at the database level.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(450)]
    public string? UserId { get; set; }

    [MaxLength(256)]
    public string? UserName { get; set; }

    [MaxLength(100)]
    public string? Role { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    /// <summary>Verb, e.g. "Issue", "Return", "RfidAssign", "PolicyChange".</summary>
    [Required, MaxLength(100)]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? EntityType { get; set; }

    [MaxLength(100)]
    public string? EntityId { get; set; }

    /// <summary>JSON snapshots. Must never contain CNIC, passwords or tokens (sections 44, 56).</summary>
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }

    public int? RfidReaderId { get; set; }

    [MaxLength(128)]
    public string? RfidEpc { get; set; }

    [MaxLength(60)]
    public string? TransactionNumber { get; set; }

    public bool Succeeded { get; set; } = true;

    [MaxLength(600)]
    public string? FailureReason { get; set; }

    [MaxLength(64)]
    public string? CorrelationId { get; set; }
}

/// <summary>Specification sections 28, 55.</summary>
public class SecurityEvent
{
    public long Id { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    public SecurityEventKind Kind { get; set; }
    public SecurityEventSeverity Severity { get; set; } = SecurityEventSeverity.Warning;

    public int? ReaderId { get; set; }
    public RfidReader? Reader { get; set; }

    [MaxLength(128)]
    public string? Epc { get; set; }

    public int? StudentId { get; set; }
    public int? BookCopyId { get; set; }

    [Required, MaxLength(600)]
    public string Description { get; set; } = string.Empty;

    public bool IsAcknowledged { get; set; }

    [MaxLength(450)]
    public string? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }

    [MaxLength(64)]
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Outbox row (specification section 53). A circulation transaction commits, then a notification
/// row is written in the same transaction; a background processor sends it later.
///
/// This is what stops email failure from breaking book issuance (specification sections 51, 87).
/// </summary>
public class Notification
{
    public long Id { get; set; }

    public NotificationKind Kind { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public int? StudentId { get; set; }
    public Student? Student { get; set; }

    [MaxLength(256)]
    public string? Recipient { get; set; }

    [MaxLength(300)]
    public string? Subject { get; set; }

    public string? Body { get; set; }

    public int? BorrowingTransactionId { get; set; }
    public int? ReservationId { get; set; }
    public int? FineId { get; set; }

    /// <summary>
    /// Idempotency key, unique per logical notification. Prevents duplicate sends after an
    /// app-pool recycle re-runs a catch-up pass (specification section 24, DEPLOYMENT.md section 2).
    /// </summary>
    [Required, MaxLength(200)]
    public string DeduplicationKey { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When this becomes eligible to send. Drives catch-up rather than tick-based scheduling.</summary>
    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? SentUtc { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    [MaxLength(64)]
    public string? CorrelationId { get; set; }
}

public class NotificationTemplate
{
    public int Id { get; set; }

    public NotificationKind Kind { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;

    [Required, MaxLength(300)]
    public string SubjectTemplate { get; set; } = string.Empty;

    [Required]
    public string BodyTemplate { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    [MaxLength(150)]
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
