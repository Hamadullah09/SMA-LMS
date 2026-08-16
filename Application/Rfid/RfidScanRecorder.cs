using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Rfid.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Rfid;

/// <summary>What a scanned EPC turned out to be.</summary>
public sealed record ScanResolution(
    long ScanEventId,
    RfidTagKind? Kind,
    int? StudentId,
    int? BookCopyId,
    string Description,
    string CorrelationId)
{
    public bool IsUnknown => Kind is null;
}

/// <summary>
/// Persists deduplicated scans and resolves them to entities
/// (specification sections 4E, 4I, 55, 83).
///
/// This is the missing link between the scan pipeline and the database: without it the reader
/// produces events that vanish, so the RFID activity report has nothing to show and a security
/// event has no scan to point at.
///
/// An unknown tag is still recorded. That is the point of section 55 — a tag nobody recognises
/// appearing at a gate is exactly the thing worth having a row for.
/// </summary>
public interface IRfidScanRecorder
{
    Task<ScanResolution> RecordAsync(RfidScan scan, CancellationToken ct = default);

    /// <summary>
    /// Writes back the final read count for bursts that have ended, and extends their
    /// last-observed timestamp to when the tag actually left the field.
    /// </summary>
    Task<int> ApplyBurstCompletionsAsync(
        IReadOnlyList<RfidBurstCompletion> completions, CancellationToken ct = default);
}

public sealed class RfidScanRecorder : IRfidScanRecorder
{
    private readonly ApplicationDbContext _db;
    private readonly IRfidScanProcessor _processor;
    private readonly ILogger<RfidScanRecorder> _logger;

    public RfidScanRecorder(
        ApplicationDbContext db,
        IRfidScanProcessor processor,
        ILogger<RfidScanRecorder> logger)
    {
        _db = db;
        _processor = processor;
        _logger = logger;
    }

    public async Task<ScanResolution> RecordAsync(RfidScan scan, CancellationToken ct = default)
    {
        var epc = scan.Epc.Trim().ToUpperInvariant();

        // Only ACTIVE assignments resolve: a replaced card must not identify its old owner.
        var studentTag = await _db.StudentRfidTags
            .AsNoTracking()
            .Include(t => t.Student)
            .FirstOrDefaultAsync(t => t.IsActive && t.Epc == epc, ct);

        BookRfidTag? bookTag = null;
        if (studentTag is null)
        {
            bookTag = await _db.BookRfidTags
                .AsNoTracking()
                .Include(t => t.BookCopy).ThenInclude(c => c!.Book)
                .FirstOrDefaultAsync(t => t.IsActive && t.Epc == epc, ct);
        }

        var kind = studentTag is not null ? RfidTagKind.StudentCard
            : bookTag is not null ? RfidTagKind.BookCopy
            : (RfidTagKind?)null;

        var scanEvent = new RfidScanEvent
        {
            ReaderId = scan.ReaderId,
            Epc = epc,
            Rssi = scan.Rssi,
            Antenna = scan.Antenna,
            FirstObservedUtc = scan.FirstObservedUtc,
            LastObservedUtc = scan.LastObservedUtc,
            ReadCount = scan.ReadCount,
            ResolvedKind = kind,
            ResolvedStudentId = studentTag?.StudentId,
            ResolvedBookCopyId = bookTag?.BookCopyId,
            CorrelationId = scan.CorrelationId
        };

        _db.RfidScanEvents.Add(scanEvent);

        // Keep the reader's health current: a scan is proof it is alive.
        var reader = await _db.RfidReaders.FirstOrDefaultAsync(r => r.Id == scan.ReaderId, ct);
        if (reader is not null)
        {
            reader.LastScanUtc = scan.LastObservedUtc;
            reader.LastHeartbeatUtc = scan.LastObservedUtc;
            reader.Status = RfidReaderStatus.Online;
            reader.ConsecutiveFailures = 0;
        }

        // An unrecognised tag is a security-relevant fact, not a silent miss (§55).
        if (kind is null)
        {
            _db.SecurityEvents.Add(new SecurityEvent
            {
                Kind = SecurityEventKind.UnknownTag,
                Severity = reader?.Purpose == RfidReaderPurpose.SecurityGate
                    ? SecurityEventSeverity.Critical
                    : SecurityEventSeverity.Info,
                ReaderId = scan.ReaderId,
                Epc = epc,
                Description = $"Unrecognised tag {epc} observed at {reader?.Name ?? "an unknown reader"}.",
                CorrelationId = scan.CorrelationId
            });
        }

        // Record the last time each tag was seen, useful for inventory and for spotting
        // a card that has quietly stopped working.
        if (studentTag is not null)
        {
            var tracked = await _db.StudentRfidTags.FirstAsync(t => t.Id == studentTag.Id, ct);
            tracked.LastScannedUtc = scan.LastObservedUtc;
            tracked.LastScannedReaderId = scan.ReaderId;
        }
        else if (bookTag is not null)
        {
            var tracked = await _db.BookRfidTags.FirstAsync(t => t.Id == bookTag.Id, ct);
            tracked.LastScannedUtc = scan.LastObservedUtc;
            tracked.LastScannedReaderId = scan.ReaderId;

            var copy = await _db.BookCopies.FirstOrDefaultAsync(c => c.Id == bookTag.BookCopyId, ct);
            if (copy is not null)
            {
                copy.LastSeenUtc = scan.LastObservedUtc;
                copy.LastSeenReaderId = scan.ReaderId;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Tell the processor which row this burst belongs to, so the flush service can write the
        // final read count back once the tag leaves the field.
        _processor.AttachScanEvent(scan.ReaderId, epc, scanEvent.Id);

        var description = studentTag is not null
            ? $"{studentTag.Student!.FullName} ({studentTag.Student.RollNumber})"
            : bookTag is not null
                ? $"{bookTag.BookCopy!.Book?.Title} — copy {bookTag.BookCopy.CopyNumber}"
                : "Unknown tag";

        if (kind is null)
        {
            _logger.LogWarning("Unrecognised tag {Epc} at reader {ReaderId}.", epc, scan.ReaderId);
        }

        return new ScanResolution(
            scanEvent.Id, kind, studentTag?.StudentId, bookTag?.BookCopyId, description, scan.CorrelationId);
    }

    public async Task<int> ApplyBurstCompletionsAsync(
        IReadOnlyList<RfidBurstCompletion> completions, CancellationToken ct = default)
    {
        if (completions.Count == 0)
        {
            return 0;
        }

        var ids = completions.Select(c => c.ScanEventId).ToList();

        var events = await _db.RfidScanEvents
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);

        var byId = completions.ToDictionary(c => c.ScanEventId);
        var updated = 0;

        foreach (var scanEvent in events)
        {
            var completion = byId[scanEvent.Id];

            // Guard against a flush arriving out of order behind a larger one.
            if (completion.ReadCount <= scanEvent.ReadCount)
            {
                continue;
            }

            scanEvent.ReadCount = completion.ReadCount;
            scanEvent.LastObservedUtc = completion.LastObservedUtc;
            updated++;
        }

        if (updated > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogDebug("Wrote back read counts for {Count} completed burst(s).", updated);
        }

        return updated;
    }
}
