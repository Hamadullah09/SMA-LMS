using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Application.Security;

/// <summary>What the gate decided about one observation.</summary>
public sealed record GateVerdict(bool IsViolation, string? Description)
{
    public static readonly GateVerdict Allowed = new(false, null);
}

/// <summary>
/// Exit-gate enforcement (specification sections 28, 29, 55).
///
/// The rule is narrow: a copy observed at an exit reader must have an open loan. A book on the shelf
/// has no loan, so a book leaving the building without one is a book leaving without being borrowed.
///
/// Deliberately keyed on the reader's <see cref="RfidReaderPurpose"/> rather than applied everywhere.
/// The same observation means opposite things at different readers: an un-issued copy on the
/// self-checkout pad is the normal first step of borrowing, while at the door it is theft. A single
/// reader cannot be both, and treating a checkout pad as a gate would alarm on every honest borrow.
/// </summary>
public interface IGateSecurityService
{
    /// <summary>
    /// Evaluates a resolved book scan. Returns <see cref="GateVerdict.Allowed"/> when the reader is
    /// not a gate, so callers can invoke this for every scan without checking first.
    /// </summary>
    Task<GateVerdict> EvaluateBookAsync(
        int readerId, int bookCopyId, string epc, string? correlationId, CancellationToken ct = default);

    /// <summary>Is this reader acting as an exit gate?</summary>
    Task<bool> IsGateAsync(int readerId, CancellationToken ct = default);
}

public sealed class GateSecurityService : IGateSecurityService
{
    private readonly ApplicationDbContext _db;
    private readonly ISecurityAlarm _alarm;
    private readonly SecurityAlarmOptions _options;
    private readonly ILogger<GateSecurityService> _logger;

    public GateSecurityService(
        ApplicationDbContext db,
        ISecurityAlarm alarm,
        IOptions<SecurityAlarmOptions> options,
        ILogger<GateSecurityService> logger)
    {
        _db = db;
        _alarm = alarm;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsGateAsync(int readerId, CancellationToken ct = default)
    {
        if (_options.TreatAllReadersAsGate)
        {
            return true;
        }

        return await _db.RfidReaders
            .AsNoTracking()
            .AnyAsync(r => r.Id == readerId && r.Purpose == RfidReaderPurpose.SecurityGate, ct);
    }

    public async Task<GateVerdict> EvaluateBookAsync(
        int readerId, int bookCopyId, string epc, string? correlationId, CancellationToken ct = default)
    {
        if (!await IsGateAsync(readerId, ct))
        {
            return GateVerdict.Allowed;
        }

        var copy = await _db.BookCopies
            .AsNoTracking()
            .Include(c => c.Book)
            .FirstOrDefaultAsync(c => c.Id == bookCopyId, ct);

        if (copy is null)
        {
            return GateVerdict.Allowed;
        }

        // An open loan is the whole test. Nothing else about the copy matters: a book may be flagged
        // damaged or missing and still be legitimately checked out to somebody.
        var loan = await _db.BorrowingRecords
            .AsNoTracking()
            .Include(r => r.Student)
            .FirstOrDefaultAsync(r => r.BookCopyId == bookCopyId && r.ReturnDate == null, ct);

        if (loan is not null)
        {
            return GateVerdict.Allowed;
        }

        var title = copy.Book?.Title ?? "Untitled";
        var where = copy.AccessionNumber is { Length: > 0 } acc ? $" ({acc})" : string.Empty;

        var description =
            $"\"{title}\" copy {copy.CopyNumber}{where} passed the exit gate with no loan on record. "
            + $"Status was {copy.Status}.";

        var reader = await _db.RfidReaders
            .AsNoTracking()
            .Where(r => r.Id == readerId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct);

        _db.SecurityEvents.Add(new SecurityEvent
        {
            Kind = SecurityEventKind.GateExitWithoutIssue,
            Severity = SecurityEventSeverity.Critical,
            ReaderId = readerId,
            Epc = epc,
            BookCopyId = bookCopyId,
            Description = $"{description} Detected at {reader ?? "an exit gate"}.",
            CorrelationId = correlationId
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "GATE VIOLATION: copy {CopyId} (\"{Title}\") left via reader {ReaderId} with no open loan.",
            bookCopyId, title, readerId);

        // Sounding is separate from recording on purpose: the record is the thing that must not be
        // lost, and it is already committed by the time anything tries to make a noise.
        await _alarm.SoundAsync(readerId, description, ct);

        return new GateVerdict(true, description);
    }
}
