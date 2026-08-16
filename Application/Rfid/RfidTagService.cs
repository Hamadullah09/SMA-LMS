using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Rfid;

public sealed record TagAssignmentResult(bool Succeeded, string Message, string? PreviousHolder = null)
{
    public static TagAssignmentResult Ok(string message) => new(true, message);
    public static TagAssignmentResult Fail(string message, string? holder = null) => new(false, message, holder);
}

/// <summary>What an EPC currently maps to, used to warn before assigning.</summary>
public sealed record TagLookup(bool IsKnown, RfidTagKind? Kind, string? HolderDescription, int? HolderId);

/// <summary>
/// RFID tag assignment (specification sections 36, 37, 4F).
///
/// Two rules dominate and are enforced here rather than in a controller:
///   * a live EPC may belong to exactly one entity - assigning a card already held by another
///     student is blocked, never silently reassigned (§4F)
///   * history is never deleted - replacing a card ends the old assignment and inserts a new
///     row, so a lost card remains auditable (§87)
/// </summary>
public interface IRfidTagService
{
    Task<TagLookup> LookupAsync(string epc, CancellationToken ct = default);
    Task<TagAssignmentResult> AssignStudentCardAsync(int studentId, string epc, string? actor, CancellationToken ct = default);
    Task<TagAssignmentResult> AssignBookTagAsync(int bookCopyId, string epc, string? actor, CancellationToken ct = default);
    Task<TagAssignmentResult> RevokeStudentCardAsync(int studentId, string reason, string? actor, CancellationToken ct = default);
}

public sealed class RfidTagService : IRfidTagService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<RfidTagService> _logger;

    public RfidTagService(ApplicationDbContext db, ILogger<RfidTagService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<TagLookup> LookupAsync(string epc, CancellationToken ct = default)
    {
        var value = Normalise(epc);

        var studentTag = await _db.StudentRfidTags
            .AsNoTracking()
            .Include(t => t.Student)
            .FirstOrDefaultAsync(t => t.IsActive && t.Epc == value, ct);

        if (studentTag is not null)
        {
            return new TagLookup(true, RfidTagKind.StudentCard,
                $"{studentTag.Student!.FullName} ({studentTag.Student.RollNumber})", studentTag.StudentId);
        }

        var bookTag = await _db.BookRfidTags
            .AsNoTracking()
            .Include(t => t.BookCopy).ThenInclude(c => c!.Book)
            .FirstOrDefaultAsync(t => t.IsActive && t.Epc == value, ct);

        if (bookTag is not null)
        {
            return new TagLookup(true, RfidTagKind.BookCopy,
                $"{bookTag.BookCopy!.Book?.Title} (copy {bookTag.BookCopy.CopyNumber})", bookTag.BookCopyId);
        }

        return new TagLookup(false, null, null, null);
    }

    public async Task<TagAssignmentResult> AssignStudentCardAsync(
        int studentId, string epc, string? actor, CancellationToken ct = default)
    {
        var value = Normalise(epc);
        if (string.IsNullOrWhiteSpace(value))
        {
            return TagAssignmentResult.Fail("Scan a card before confirming.");
        }

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null)
        {
            return TagAssignmentResult.Fail("That student record no longer exists.");
        }

        // A live EPC belongs to exactly one holder. Refuse rather than reassign (§4F).
        var existing = await LookupAsync(value, ct);
        if (existing.IsKnown && !(existing.Kind == RfidTagKind.StudentCard && existing.HolderId == studentId))
        {
            return existing.Kind == RfidTagKind.StudentCard
                ? TagAssignmentResult.Fail(
                    "This RFID card is already assigned to another student.", existing.HolderDescription)
                : TagAssignmentResult.Fail(
                    "This tag is already attached to a book and cannot be used as a student card.",
                    existing.HolderDescription);
        }

        if (existing.IsKnown && existing.HolderId == studentId)
        {
            return TagAssignmentResult.Fail("This card is already assigned to this student.");
        }

        var now = DateTime.UtcNow;

        // Replacement: end the current card, never delete it (§87).
        var current = await _db.StudentRfidTags
            .Where(t => t.StudentId == studentId && t.IsActive)
            .ToListAsync(ct);

        foreach (var old in current)
        {
            old.End(RfidTagState.Replaced, actor, "Replaced by a newly issued card", now);
        }

        _db.StudentRfidTags.Add(new StudentRfidTag
        {
            StudentId = studentId,
            Epc = value,
            State = RfidTagState.Active,
            IsActive = true,
            AssignedBy = actor,
            AssignedUtc = now
        });

        await WriteAuditAsync("RfidAssign", "Student", studentId.ToString(), value, actor,
            current.Count > 0 ? "Card replaced" : "Card issued", ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Card {Epc} assigned to student {StudentId} by {Actor}.", value, studentId, actor);

        return TagAssignmentResult.Ok(current.Count > 0
            ? $"Card replaced. The previous card is now marked replaced and remains in the audit history."
            : $"Card assigned to {student.FullName}.");
    }

    public async Task<TagAssignmentResult> AssignBookTagAsync(
        int bookCopyId, string epc, string? actor, CancellationToken ct = default)
    {
        var value = Normalise(epc);
        if (string.IsNullOrWhiteSpace(value))
        {
            return TagAssignmentResult.Fail("Scan a tag before confirming.");
        }

        var copy = await _db.BookCopies
            .Include(c => c.Book)
            .FirstOrDefaultAsync(c => c.Id == bookCopyId, ct);

        if (copy is null)
        {
            return TagAssignmentResult.Fail("That copy no longer exists.");
        }

        var existing = await LookupAsync(value, ct);
        if (existing.IsKnown && !(existing.Kind == RfidTagKind.BookCopy && existing.HolderId == bookCopyId))
        {
            return existing.Kind == RfidTagKind.BookCopy
                ? TagAssignmentResult.Fail(
                    "This tag is already attached to another book copy.", existing.HolderDescription)
                : TagAssignmentResult.Fail(
                    "This tag is a student card and cannot be attached to a book.", existing.HolderDescription);
        }

        if (existing.IsKnown && existing.HolderId == bookCopyId)
        {
            return TagAssignmentResult.Fail("This tag is already attached to this copy.");
        }

        var now = DateTime.UtcNow;

        var current = await _db.BookRfidTags
            .Where(t => t.BookCopyId == bookCopyId && t.IsActive)
            .ToListAsync(ct);

        foreach (var old in current)
        {
            old.End(RfidTagState.Replaced, actor, "Replaced by a newly attached tag", now);
        }

        _db.BookRfidTags.Add(new BookRfidTag
        {
            BookCopyId = bookCopyId,
            Epc = value,
            State = RfidTagState.Active,
            IsActive = true,
            AssignedBy = actor,
            AssignedUtc = now
        });

        await WriteAuditAsync("RfidAssign", "BookCopy", bookCopyId.ToString(), value, actor,
            current.Count > 0 ? "Tag replaced" : "Tag attached", ct);

        await _db.SaveChangesAsync(ct);

        return TagAssignmentResult.Ok(
            $"Tag attached to {copy.Book?.Title} copy {copy.CopyNumber}.");
    }

    public async Task<TagAssignmentResult> RevokeStudentCardAsync(
        int studentId, string reason, string? actor, CancellationToken ct = default)
    {
        var current = await _db.StudentRfidTags
            .Where(t => t.StudentId == studentId && t.IsActive)
            .ToListAsync(ct);

        if (current.Count == 0)
        {
            return TagAssignmentResult.Fail("This student has no active card to revoke.");
        }

        var state = reason.Equals("lost", StringComparison.OrdinalIgnoreCase)
            ? RfidTagState.Lost
            : reason.Equals("damaged", StringComparison.OrdinalIgnoreCase)
                ? RfidTagState.Damaged
                : RfidTagState.Revoked;

        var now = DateTime.UtcNow;
        foreach (var tag in current)
        {
            tag.End(state, actor, reason, now);
        }

        await WriteAuditAsync("RfidRevoke", "Student", studentId.ToString(),
            current[0].Epc, actor, $"Card {state}", ct);

        await _db.SaveChangesAsync(ct);

        return TagAssignmentResult.Ok(
            $"Card marked {state.ToString().ToLowerInvariant()}. The student can no longer be identified by it.");
    }

    /// <summary>EPCs are hex; normalising avoids a case mismatch defeating the uniqueness index.</summary>
    private static string Normalise(string epc) => (epc ?? string.Empty).Trim().ToUpperInvariant();

    private Task WriteAuditAsync(
        string operation, string entityType, string entityId, string epc, string? actor, string note, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Operation = operation,
            EntityType = entityType,
            EntityId = entityId,
            RfidEpc = epc,
            UserName = actor,
            NewValue = note,
            Succeeded = true,
            OccurredUtc = DateTime.UtcNow
        });

        return Task.CompletedTask;
    }
}
