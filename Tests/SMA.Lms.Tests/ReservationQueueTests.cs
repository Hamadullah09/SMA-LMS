using Library_Management_system.Application.Circulation;
using Library_Management_system.Application.Notifications;
using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// Reservation queue behaviour (specification section 26).
///
/// Carried over as owed from an earlier phase: the fulfilment-on-return path was implemented and
/// verified by inspection but never executed by a test, because the seeded data had no title with
/// every copy simultaneously on loan. These build that state explicitly.
///
/// Uses the in-memory provider, so this covers queue LOGIC, not the SQL constraints — those are
/// verified separately against SQL Server.
/// </summary>
public class ReservationQueueTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly ReservationService _service;
    private readonly RecordingOutbox _outbox = new();

    public ReservationQueueTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"res-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ApplicationDbContext(options);

        _service = new ReservationService(
            _db,
            new LibraryPolicyService(_db, new MemoryCache(new MemoryCacheOptions())),
            _outbox,
            NullLogger<ReservationService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Captures what would have been emailed, without needing SMTP.</summary>
    private sealed class RecordingOutbox : INotificationOutbox
    {
        public List<(NotificationKind Kind, int StudentId, string Key)> Sent { get; } = [];

        public void Enqueue(NotificationKind kind, int studentId, string recipient, string subject,
            string body, string deduplicationKey, DateTime? sendAfterUtc = null,
            int? borrowingRecordId = null, string? correlationId = null)
            => Sent.Add((kind, studentId, deduplicationKey));

        public Task<int> DispatchDueAsync(int batchSize, CancellationToken ct = default) => Task.FromResult(0);
    }

    private async Task<(int BookId, int CopyId, int[] StudentIds)> SeedAsync(
        int copies, int copiesOnLoan, int students)
    {
        var book = new Library_Management_system.Models.Book { Title = "Foundation", Author = "Isaac Asimov" };
        _db.Books.Add(book);
        await _db.SaveChangesAsync();

        var created = new List<BookCopy>();
        for (var i = 0; i < copies; i++)
        {
            var copy = new BookCopy
            {
                BookId = book.Id,
                CopyNumber = $"{i + 1:D3}",
                Status = i < copiesOnLoan ? BookCopyStatus.Issued : BookCopyStatus.Available
            };
            created.Add(copy);
            _db.BookCopies.Add(copy);
        }

        var ids = new List<int>();
        for (var i = 0; i < students; i++)
        {
            var student = new Student
            {
                FullName = $"Student {i + 1}",
                RollNumber = $"R-{i + 1:D3}",
                StudentIdNumber = $"S-{i + 1:D3}",
                Email = $"s{i + 1}@example.edu",
                Status = StudentStatus.Active
            };
            _db.Students.Add(student);
            await _db.SaveChangesAsync();
            ids.Add(student.Id);
        }

        await _db.SaveChangesAsync();
        return (book.Id, created[0].Id, ids.ToArray());
    }

    // ---------------------------------------------------------------- queueing

    [Fact]
    public async Task Reserving_a_title_with_a_copy_on_the_shelf_is_refused()
    {
        var (bookId, _, students) = await SeedAsync(copies: 2, copiesOnLoan: 1, students: 1);

        var result = await _service.ReserveAsync(students[0], bookId);

        Assert.False(result.Succeeded);
        Assert.Contains("on the shelf right now", result.Message);
    }

    [Fact]
    public async Task Reserving_a_fully_borrowed_title_joins_the_queue()
    {
        var (bookId, _, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 1);

        var result = await _service.ReserveAsync(students[0], bookId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.QueuePosition);
    }

    [Fact]
    public async Task Queue_positions_are_allocated_first_come_first_served()
    {
        var (bookId, _, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 3);

        var first = await _service.ReserveAsync(students[0], bookId);
        var second = await _service.ReserveAsync(students[1], bookId);
        var third = await _service.ReserveAsync(students[2], bookId);

        Assert.Equal(1, first.QueuePosition);
        Assert.Equal(2, second.QueuePosition);
        Assert.Equal(3, third.QueuePosition);
    }

    [Fact]
    public async Task A_student_cannot_queue_twice_for_the_same_title()
    {
        var (bookId, _, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 1);

        await _service.ReserveAsync(students[0], bookId);
        var again = await _service.ReserveAsync(students[0], bookId);

        Assert.False(again.Succeeded);
        Assert.Contains("already have a reservation", again.Message);
    }

    [Fact]
    public async Task Cancelling_closes_the_gap_in_the_queue()
    {
        var (bookId, _, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 3);

        await _service.ReserveAsync(students[0], bookId);
        await _service.ReserveAsync(students[1], bookId);
        await _service.ReserveAsync(students[2], bookId);

        var second = await _db.Reservations.FirstAsync(r => r.StudentId == students[1]);
        await _service.CancelAsync(second.Id, students[1]);

        // The third student must move up rather than being stranded at position 3.
        var third = await _db.Reservations.FirstAsync(r => r.StudentId == students[2]);
        Assert.Equal(2, third.QueuePosition);
    }

    [Fact]
    public async Task One_student_cannot_cancel_another_students_hold()
    {
        // Specification section 43: student isolation.
        var (bookId, _, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 2);

        await _service.ReserveAsync(students[0], bookId);
        var victim = await _db.Reservations.FirstAsync(r => r.StudentId == students[0]);

        var attack = await _service.CancelAsync(victim.Id, students[1]);

        Assert.False(attack.Succeeded);
        Assert.Equal(ReservationStatus.Queued,
            (await _db.Reservations.FirstAsync(r => r.Id == victim.Id)).Status);
    }

    // ---------------------------------------------------------------- fulfilment

    [Fact]
    public async Task Returned_copy_goes_to_the_first_student_in_the_queue()
    {
        var (bookId, copyId, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 2);

        await _service.ReserveAsync(students[0], bookId);
        await _service.ReserveAsync(students[1], bookId);

        var fulfilled = await _service.FulfilNextAsync(bookId, copyId);
        await _db.SaveChangesAsync();

        Assert.NotNull(fulfilled);
        Assert.Equal(students[0], fulfilled!.StudentId);
        Assert.Equal(ReservationStatus.Available, fulfilled.Status);
        Assert.Equal(copyId, fulfilled.ReservedCopyId);
    }

    [Fact]
    public async Task A_held_copy_is_marked_reserved_so_it_is_not_issued_to_someone_else()
    {
        var (bookId, copyId, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 1);

        await _service.ReserveAsync(students[0], bookId);
        await _service.FulfilNextAsync(bookId, copyId);
        await _db.SaveChangesAsync();

        var copy = await _db.BookCopies.FirstAsync(c => c.Id == copyId);
        Assert.Equal(BookCopyStatus.Reserved, copy.Status);
    }

    [Fact]
    public async Task Fulfilment_emails_the_student_and_sets_a_collection_deadline()
    {
        var (bookId, copyId, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 1);

        await _service.ReserveAsync(students[0], bookId);
        var fulfilled = await _service.FulfilNextAsync(bookId, copyId);
        await _db.SaveChangesAsync();

        Assert.Contains(_outbox.Sent, s =>
            s.Kind == NotificationKind.ReservationAvailable && s.StudentId == students[0]);
        Assert.NotNull(fulfilled!.ExpiresUtc);
        Assert.True(fulfilled.ExpiresUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Returning_a_title_nobody_wants_fulfils_nothing()
    {
        var (bookId, copyId, _) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 1);

        Assert.Null(await _service.FulfilNextAsync(bookId, copyId));
    }

    [Fact]
    public async Task An_uncollected_hold_expires_and_passes_to_the_next_student()
    {
        var (bookId, copyId, students) = await SeedAsync(copies: 1, copiesOnLoan: 1, students: 2);

        await _service.ReserveAsync(students[0], bookId);
        await _service.ReserveAsync(students[1], bookId);

        await _service.FulfilNextAsync(bookId, copyId);
        await _db.SaveChangesAsync();

        // The first student never came for it.
        var held = await _db.Reservations.FirstAsync(r => r.StudentId == students[0]);
        held.ExpiresUtc = DateTime.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync();

        var expired = await _service.ExpireStaleAsync();

        Assert.Equal(1, expired);
        Assert.Equal(ReservationStatus.Expired,
            (await _db.Reservations.FirstAsync(r => r.Id == held.Id)).Status);

        // The copy must not be stranded — the second student now holds it.
        var next = await _db.Reservations.FirstAsync(r => r.StudentId == students[1]);
        Assert.Equal(ReservationStatus.Available, next.Status);
        Assert.Equal(copyId, next.ReservedCopyId);
    }
}
