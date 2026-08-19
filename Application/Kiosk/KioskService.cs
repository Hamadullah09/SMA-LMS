using Library_Management_system.Application.Circulation;
using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Rfid.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Application.Kiosk;

/// <summary>
/// The self-service station's state machine.
///
/// Every borrowing rule still belongs to <see cref="ICirculationService"/> — the same service the
/// staff desk calls (specification sections 71, 87). This class decides only what the station is
/// showing: which scans belong to the current student, what the pad currently holds, and when the
/// station should forget it all.
///
/// Two things a one-book-at-a-time desk never had to handle, and which are the substance here:
///
///   * a <em>basket</em>. Per-copy validation cannot see that the student is about to borrow four
///     books when their limit is three, or that two copies of the same title are on the pad. Those
///     are checked across the basket, so the student is told at the pad rather than discovering it
///     as a partial failure after pressing confirm.
///
///   * a student who <em>walks away</em>. A desk has a librarian; a kiosk has nobody, so the station
///     clears itself on an idle timer and never leaves an identified account open for the next
///     person who wanders up.
/// </summary>
public interface IKioskService
{
    /// <summary>Reader the kiosk binds to when the URL does not name one.</summary>
    Task<int?> ResolveDefaultReaderIdAsync(CancellationToken ct = default);

    /// <summary>Drain new scans into the station and return what the screen should show.</summary>
    Task<KioskState> RefreshAsync(int readerId, CancellationToken ct = default);

    /// <summary>
    /// Identifies the station's student from a signed-in account rather than a card tap, for a
    /// student arriving from their own cart. Does nothing if the station already has a student —
    /// a card physically presented at the pad always outranks a browser cookie.
    /// </summary>
    Task<KioskState> AdoptSignedInStudentAsync(
        int readerId, string applicationUserId, string? email, CancellationToken ct = default);

    Task<KioskState> SetModeAsync(int readerId, KioskMode mode, CancellationToken ct = default);
    Task<KioskState> RemoveItemAsync(int readerId, int bookCopyId, CancellationToken ct = default);
    Task<KioskState> ResetAsync(int readerId, CancellationToken ct = default);

    /// <summary>Issue or return everything allowed in the basket, and produce the receipt.</summary>
    Task<KioskState> CommitAsync(int readerId, CancellationToken ct = default);
}

public sealed class KioskService : IKioskService
{
    /// <summary>How long a finished receipt stays up before the station returns to idle.</summary>
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromSeconds(45);

    private readonly ApplicationDbContext _db;
    private readonly ICirculationService _circulation;
    private readonly ILibraryPolicyService _policies;
    private readonly IRfidLiveFeed _feed;
    private readonly KioskStationStore _stations;
    private readonly KioskOptions _options;
    private readonly Library_Management_system.Rfid.RfidOptions _rfid;
    private readonly ILogger<KioskService> _logger;

    public KioskService(
        ApplicationDbContext db,
        ICirculationService circulation,
        ILibraryPolicyService policies,
        IRfidLiveFeed feed,
        KioskStationStore stations,
        IOptions<KioskOptions> options,
        IOptions<Library_Management_system.Rfid.RfidOptions> rfid,
        ILogger<KioskService> logger)
    {
        _db = db;
        _circulation = circulation;
        _policies = policies;
        _feed = feed;
        _stations = stations;
        _options = options.Value;
        _rfid = rfid.Value;
        _logger = logger;
    }

    public async Task<int?> ResolveDefaultReaderIdAsync(CancellationToken ct = default)
    {
        // Purpose first: a library with a gate reader and a desk reader must not have the kiosk
        // silently bind to the gate.
        var byPurpose = await _db.RfidReaders
            .AsNoTracking()
            .Where(r => r.IsEnabled && r.Purpose == RfidReaderPurpose.Checkout)
            .OrderBy(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct);

        if (byPurpose is not null)
        {
            return byPurpose;
        }

        return await _db.RfidReaders
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct);
    }

    // ------------------------------------------------------------------ entry points

    public Task<KioskState> RefreshAsync(int readerId, CancellationToken ct = default) =>
        WithStationAsync(readerId, async station =>
        {
            ExpireIfIdle(station);
            await DrainFeedAsync(station, ct);
        }, ct);

    public Task<KioskState> AdoptSignedInStudentAsync(
        int readerId, string applicationUserId, string? email, CancellationToken ct = default) =>
        WithStationAsync(readerId, async station =>
        {
            if (!_options.AllowSignedInIdentity || station.StudentId is not null)
            {
                return;
            }

            // The account link is authoritative. Email is a fallback for a student record imported
            // from the registry before the person ever signed in (§35), which is the normal case.
            var student = await _db.Students
                .AsNoTracking()
                .Include(s => s.Department)
                .FirstOrDefaultAsync(s => s.ApplicationUserId == applicationUserId, ct);

            if (student is null && !string.IsNullOrWhiteSpace(email))
            {
                student = await _db.Students
                    .AsNoTracking()
                    .Include(s => s.Department)
                    .FirstOrDefaultAsync(s => s.Email == email, ct);
            }

            if (student is null)
            {
                // A librarian or admin opening the kiosk is not a borrower. Say so plainly rather
                // than leaving a station that looks broken.
                station.AddNotice(
                    "This account has no student record, so it cannot borrow. Tap a student card instead.");
                station.Touch();
                return;
            }

            ApplyStudent(station, student);
            station.Touch();
            await RevalidateAsync(station, ct);
        }, ct);

    public Task<KioskState> SetModeAsync(int readerId, KioskMode mode, CancellationToken ct = default) =>
        WithStationAsync(readerId, async station =>
        {
            if (station.Mode != mode)
            {
                // Switching between borrowing and returning invalidates the basket: the same copy
                // means opposite things in the two modes.
                station.Items.Clear();
                station.Notices.Clear();
                station.Receipt = null;
                station.ReceiptShownUtc = null;
                station.Mode = mode;
            }

            station.Touch();
            await DrainFeedAsync(station, ct);
        }, ct);

    public Task<KioskState> RemoveItemAsync(int readerId, int bookCopyId, CancellationToken ct = default) =>
        WithStationAsync(readerId, async station =>
        {
            if (station.Items.Remove(bookCopyId))
            {
                station.Touch();
                await RevalidateAsync(station, ct);
            }

            // Do not drain here. The book is probably still on the pad, and re-reading it would put
            // straight back the line the student just removed.
        }, ct);

    public Task<KioskState> ResetAsync(int readerId, CancellationToken ct = default) =>
        WithStationAsync(readerId, station =>
        {
            station.Clear();

            // Skip whatever is currently in the feed, so books still sitting on the antenna are not
            // immediately re-collected for the next student.
            station.Cursor = _feed.CurrentSequence;
            return Task.CompletedTask;
        }, ct);

    public Task<KioskState> CommitAsync(int readerId, CancellationToken ct = default) =>
        WithStationAsync(readerId, async station =>
        {
            if (station.Items.Count == 0)
            {
                station.AddNotice("There is nothing on the pad yet.");
                return;
            }

            var policy = await _policies.GetLoanPolicyAsync(ct);

            station.Receipt = station.Mode == KioskMode.Borrow
                ? await CommitBorrowAsync(station, policy, ct)
                : await CommitReturnAsync(station, policy, ct);

            station.ReceiptShownUtc = DateTime.UtcNow;
            station.Items.Clear();
            station.Notices.Clear();

            // The books just processed are still on the pad and will keep being read. Skipping past
            // them stops the receipt screen from immediately collecting them again.
            station.Cursor = _feed.CurrentSequence;
            station.Touch();
        }, ct);

    // ------------------------------------------------------------------ scan handling

    /// <summary>
    /// Applies every scan that has arrived since this station last looked. The poll from the browser
    /// is what drives this, which keeps the whole station single-threaded and easy to reason about.
    /// </summary>
    private async Task DrainFeedAsync(KioskStation station, CancellationToken ct)
    {
        var scans = _feed.Since(station.Cursor, station.ReaderId);
        if (scans.Count == 0)
        {
            return;
        }

        station.Cursor = scans[^1].Sequence;

        var changed = false;

        foreach (var scan in scans)
        {
            // A scan landing while the receipt is up means the next student has arrived. Clear the
            // receipt and start collecting rather than making them press a button first.
            if (station.Receipt is not null)
            {
                station.Clear();
            }

            switch (scan.Kind)
            {
                case RfidTagKind.StudentCard when scan.StudentId is { } studentId:
                    changed |= await IdentifyStudentAsync(station, studentId, ct);
                    break;

                case RfidTagKind.BookCopy when scan.BookCopyId is { } copyId:
                    changed |= AddPlaceholderItem(station, copyId, scan.Epc);
                    break;

                default:
                    station.AddNotice(
                        $"Tag {Shorten(scan.Epc)} is not registered. Please take this item to the desk.");
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            station.Touch();
            await RevalidateAsync(station, ct);
        }
    }

    private async Task<bool> IdentifyStudentAsync(KioskStation station, int studentId, CancellationToken ct)
    {
        if (station.StudentId == studentId)
        {
            // Same card tapped again — harmless, and no reason to redraw the screen.
            return false;
        }

        var student = await _db.Students
            .AsNoTracking()
            .Include(s => s.Department)
            .FirstOrDefaultAsync(s => s.Id == studentId, ct);

        if (student is null)
        {
            station.AddNotice("That card is registered but its student record is missing. Please see the desk.");
            return true;
        }

        // A different card mid-basket is a new person at the pad. Their books, not the last
        // student's, so the basket is theirs to keep — but the identity is replaced outright.
        ApplyStudent(station, student);
        return true;
    }

    /// <summary>
    /// Puts a student on the station. Shared by the card-tap and signed-in paths so the account
    /// warnings a librarian relies on cannot end up applying to only one of them.
    /// </summary>
    private static void ApplyStudent(KioskStation station, Domain.Entities.Student student)
    {
        station.StudentId = student.Id;
        station.StudentName = student.FullName;
        station.RollNumber = student.RollNumber;
        station.Department = student.Department?.Name;

        // Notices belong to whoever was at the pad before. Leaving them would let the station show
        // a name and "this account has no student record" at the same time — which is exactly what
        // happened when a librarian opened the kiosk and a student was identified after them. An
        // unknown tag still sitting in the field re-reads within seconds, so nothing is lost.
        station.Notices.Clear();

        station.StudentWarnings.Clear();

        if (student.Status != StudentStatus.Active)
        {
            station.StudentWarnings.Add(
                $"This account is {student.Status.ToString().ToLowerInvariant()} and cannot borrow.");
        }

        if (student.IsBorrowingBlocked)
        {
            station.StudentWarnings.Add(
                string.IsNullOrWhiteSpace(student.BorrowingBlockReason)
                    ? "Borrowing is blocked on this account. Please see the librarian."
                    : $"Borrowing is blocked: {student.BorrowingBlockReason}");
        }
    }

    /// <summary>
    /// Records that a copy is on the pad. The verdict is not worked out here because it depends on
    /// the whole basket, so <see cref="RevalidateAsync"/> fills it in once per change.
    /// </summary>
    private static bool AddPlaceholderItem(KioskStation station, int copyId, string epc)
    {
        if (station.Items.ContainsKey(copyId))
        {
            return false;
        }

        station.Items[copyId] = new KioskItem(
            copyId, epc, "…", null, string.Empty, null, false, null, DateTime.UtcNow);

        return true;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>
    /// Recomputes every line's verdict. Runs on change rather than on every poll, because a kiosk
    /// polls once a second and this touches the database several times.
    /// </summary>
    private async Task RevalidateAsync(KioskStation station, CancellationToken ct)
    {
        if (station.Items.Count == 0)
        {
            return;
        }

        var policy = await _policies.GetLoanPolicyAsync(ct);
        var copyIds = station.Items.Keys.ToList();

        var copies = await _db.BookCopies
            .AsNoTracking()
            .Include(c => c.Book)
            .Where(c => copyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        if (station.Mode == KioskMode.Return)
        {
            await RevalidateReturnsAsync(station, copies, policy, ct);
            return;
        }

        // Ordered by when it landed on the pad, so if the limit bites it is the last book placed
        // that is refused, not an arbitrary one.
        var ordered = station.Items.Values.OrderBy(i => i.AddedUtc).ToList();

        var openLoans = station.StudentId is { } sid
            ? await _db.BorrowingRecords
                .AsNoTracking()
                .Where(r => r.StudentId == sid && r.ReturnDate == null)
                .Select(r => r.BookId)
                .ToListAsync(ct)
            : [];

        var slotsLeft = Math.Max(policy.MaximumBooksPerStudent - openLoans.Count, 0);
        var titlesTaken = new HashSet<int>(openLoans);
        var accepted = 0;

        foreach (var item in ordered)
        {
            if (!copies.TryGetValue(item.BookCopyId, out var copy))
            {
                station.Items[item.BookCopyId] = item with
                {
                    Title = "Unknown item",
                    Allowed = false,
                    Message = "This copy is no longer in the catalogue."
                };
                continue;
            }

            var title = copy.Book?.Title ?? "Untitled";
            var author = copy.Book?.Author;

            // No card yet: show the book so the student can see it registered, but say what is
            // missing rather than showing a refusal they cannot act on.
            if (station.StudentId is not { } studentId)
            {
                station.Items[item.BookCopyId] = item with
                {
                    Title = title,
                    Author = author,
                    CopyNumber = copy.CopyNumber,
                    AccessionNumber = copy.AccessionNumber,
                    CoverUrl = Cover(copy),
                    Allowed = false,
                    Message = "Tap your student card to borrow this."
                };
                continue;
            }

            string? refusal = null;

            // ---- basket-wide checks, which per-copy validation cannot see ----
            if (titlesTaken.Contains(copy.BookId))
            {
                refusal = "You already have a copy of this title. Put this one back.";
            }
            else if (accepted >= slotsLeft)
            {
                refusal = openLoans.Count > 0
                    ? $"That is over your limit of {policy.MaximumBooksPerStudent} books "
                      + $"— you already have {openLoans.Count} out."
                    : $"That is over your limit of {policy.MaximumBooksPerStudent} books at once.";
            }

            // ---- the shared rules ----
            if (refusal is null)
            {
                var eligibility = await _circulation.ValidateIssueAsync(
                    studentId, copy.Id, policy.DefaultLoanDays, ct);

                if (!eligibility.IsEligible)
                {
                    refusal = eligibility.Summary;
                }
            }

            if (refusal is null)
            {
                accepted++;
                titlesTaken.Add(copy.BookId);
            }

            station.Items[item.BookCopyId] = item with
            {
                Title = title,
                Author = author,
                CopyNumber = copy.CopyNumber,
                AccessionNumber = copy.AccessionNumber,
                CoverUrl = Cover(copy),
                Allowed = refusal is null,
                Message = refusal
            };
        }
    }

    private async Task RevalidateReturnsAsync(
        KioskStation station,
        Dictionary<int, Domain.Entities.BookCopy> copies,
        LoanPolicySnapshot policy,
        CancellationToken ct)
    {
        var copyIds = station.Items.Keys.ToList();

        var loans = await _db.BorrowingRecords
            .AsNoTracking()
            .Include(r => r.Student)
            .Where(r => r.BookCopyId != null
                        && copyIds.Contains(r.BookCopyId.Value)
                        && r.ReturnDate == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var item in station.Items.Values.ToList())
        {
            copies.TryGetValue(item.BookCopyId, out var copy);
            var loan = loans.FirstOrDefault(l => l.BookCopyId == item.BookCopyId);

            var title = copy?.Book?.Title ?? "Unknown item";

            if (loan is null)
            {
                station.Items[item.BookCopyId] = item with
                {
                    Title = title,
                    Author = copy?.Book?.Author,
                    CopyNumber = copy?.CopyNumber ?? string.Empty,
                    AccessionNumber = copy?.AccessionNumber,
                    CoverUrl = Cover(copy),
                    Allowed = false,
                    Message = "This book is not on loan, so there is nothing to return."
                };
                continue;
            }

            var (overdueDays, fine) = await _circulation.CalculateFineAsync(loan.DueDate, now, ct);

            station.Items[item.BookCopyId] = item with
            {
                Title = title,
                Author = copy?.Book?.Author,
                CopyNumber = copy?.CopyNumber ?? string.Empty,
                AccessionNumber = copy?.AccessionNumber,
                CoverUrl = Cover(copy),
                Allowed = true,
                Message = overdueDays > 0
                    ? $"{overdueDays} day(s) late — {policy.Currency} {fine:0.00} to pay at the desk."
                    : null,
                OverdueDays = overdueDays,
                Fine = fine,
                DueUtc = loan.DueDate,
                BorrowerName = loan.Student?.FullName
            };
        }
    }

    // ------------------------------------------------------------------ commit

    private async Task<KioskReceipt> CommitBorrowAsync(
        KioskStation station, LoanPolicySnapshot policy, CancellationToken ct)
    {
        var lines = new List<KioskReceiptLine>();

        foreach (var item in station.Items.Values.OrderBy(i => i.AddedUtc))
        {
            if (!item.Allowed)
            {
                lines.Add(new KioskReceiptLine(
                    item.Title, item.CopyNumber, false,
                    item.Message ?? "Not issued.", null, null, 0m)
                {
                    Author = item.Author,
                    AccessionNumber = item.AccessionNumber
                });
                continue;
            }

            var result = await _circulation.IssueBookAsync(new IssueRequest(
                StudentId: station.StudentId!.Value,
                BookCopyId: item.BookCopyId,
                RequestedLoanDays: policy.DefaultLoanDays,
                Method: CirculationMethod.Rfid,
                ReaderId: station.ReaderId,
                // Self-service: no librarian was involved, and recording one would be a lie in the
                // audit trail. The reader id is what identifies where this happened.
                OperatorUserId: null), ct);

            lines.Add(new KioskReceiptLine(
                item.Title,
                item.CopyNumber,
                result.Succeeded,
                result.Summary,
                result.DueUtc,
                result.TransactionNumber,
                0m)
            {
                Author = item.Author,
                AccessionNumber = item.AccessionNumber
            });

            if (!result.Succeeded)
            {
                _logger.LogInformation(
                    "Kiosk {ReaderId} could not issue copy {CopyId} to student {StudentId}: {Reason}",
                    station.ReaderId, item.BookCopyId, station.StudentId, result.Summary);
            }
        }

        return new KioskReceipt(
            KioskMode.Borrow,
            station.StudentName ?? "Student",
            station.RollNumber ?? "—",
            DateTime.UtcNow,
            lines,
            policy.Currency)
        {
            StationName = await StationNameAsync(station.ReaderId, ct),
            Department = station.Department,
            LoanDays = policy.DefaultLoanDays,
            FinePerDay = policy.FinePerDay
        };
    }

    private async Task<KioskReceipt> CommitReturnAsync(
        KioskStation station, LoanPolicySnapshot policy, CancellationToken ct)
    {
        var lines = new List<KioskReceiptLine>();

        foreach (var item in station.Items.Values.OrderBy(i => i.AddedUtc))
        {
            if (!item.Allowed)
            {
                lines.Add(new KioskReceiptLine(
                    item.Title, item.CopyNumber, false,
                    item.Message ?? "Not returned.", null, null, 0m)
                {
                    Author = item.Author,
                    AccessionNumber = item.AccessionNumber
                });
                continue;
            }

            // Section 19: the book tag alone identifies the loan, so a student can hand back a book
            // borrowed on someone else's account without the kiosk refusing it.
            var result = await _circulation.ReturnBookAsync(new ReturnRequest(
                BookCopyId: item.BookCopyId,
                StudentId: null,
                Method: CirculationMethod.Rfid,
                ReaderId: station.ReaderId,
                OperatorUserId: null), ct);

            lines.Add(new KioskReceiptLine(
                item.Title,
                item.CopyNumber,
                result.Succeeded,
                result.Summary,
                null,
                result.TransactionNumber,
                result.FineAmount)
            {
                Author = item.Author,
                AccessionNumber = item.AccessionNumber
            });
        }

        return new KioskReceipt(
            KioskMode.Return,
            station.StudentName ?? "Returned items",
            station.RollNumber ?? "—",
            DateTime.UtcNow,
            lines,
            policy.Currency)
        {
            StationName = await StationNameAsync(station.ReaderId, ct),
            Department = station.Department,
            LoanDays = policy.DefaultLoanDays,
            FinePerDay = policy.FinePerDay
        };
    }

    private async Task<string?> StationNameAsync(int readerId, CancellationToken ct) =>
        await _db.RfidReaders
            .AsNoTracking()
            .Where(r => r.Id == readerId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ plumbing

    private void ExpireIfIdle(KioskStation station)
    {
        var idleLimit = TimeSpan.FromSeconds(Math.Max(_options.IdleTimeoutSeconds, 20));

        // A receipt clears on its own sooner than an abandoned basket does: it holds a name on
        // screen and there is nothing left to do with it.
        if (station.Receipt is not null
            && station.ReceiptShownUtc is { } shown
            && DateTime.UtcNow - shown > ReceiptLifetime)
        {
            station.Clear();
            station.Cursor = _feed.CurrentSequence;
            return;
        }

        if (station.Receipt is not null)
        {
            return;
        }

        var hasSomethingToLose = station.StudentId is not null || station.Items.Count > 0;

        if (hasSomethingToLose && DateTime.UtcNow - station.LastActivityUtc > idleLimit)
        {
            _logger.LogInformation(
                "Kiosk {ReaderId} timed out and cleared itself.", station.ReaderId);

            station.Clear();
            station.Cursor = _feed.CurrentSequence;
        }
    }

    private async Task<KioskState> WithStationAsync(
        int readerId, Func<KioskStation, Task> mutate, CancellationToken ct)
    {
        var station = _stations.Get(readerId);

        await station.Gate.WaitAsync(ct);
        try
        {
            await mutate(station);
            return await BuildStateAsync(station, ct);
        }
        finally
        {
            station.Gate.Release();
        }
    }

    private async Task<KioskState> BuildStateAsync(KioskStation station, CancellationToken ct)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);

        var reader = await _db.RfidReaders
            .AsNoTracking()
            .Where(r => r.Id == station.ReaderId)
            .Select(r => new { r.Name, r.Status, r.IsEnabled, r.LastHeartbeatUtc })
            .FirstOrDefaultAsync(ct);

        // Status alone is not evidence the reader is alive. Nothing clears it if the application is
        // killed rather than shut down, so a crashed host leaves the row reading Online — and this
        // screen would then tell a student "Reader ready" while no process is talking to it. Trust
        // the heartbeat, and allow a couple of missed beats before calling it dead.
        var heartbeatWindow = TimeSpan.FromSeconds(Math.Max(_rfid.HeartbeatSeconds, 5) * 3);

        var online = reader?.Status == RfidReaderStatus.Online
                     && reader.LastHeartbeatUtc is { } lastBeat
                     && DateTime.UtcNow - lastBeat < heartbeatWindow;

        var stage =
            reader is null || !reader.IsEnabled ? KioskStage.Unavailable
            : station.Receipt is not null ? KioskStage.Finished
            : station.StudentId is not null ? KioskStage.Collecting
            : station.Items.Count > 0 ? KioskStage.WaitingForCard
            : KioskStage.Idle;

        // Returns need no card, so a pad with books on it is a working session rather than a
        // session waiting for identification.
        if (stage == KioskStage.WaitingForCard && station.Mode == KioskMode.Return)
        {
            stage = KioskStage.Collecting;
        }

        var idleLimit = Math.Max(_options.IdleTimeoutSeconds, 20);
        var elapsed = (int)(DateTime.UtcNow - station.LastActivityUtc).TotalSeconds;

        return new KioskState(
            ReaderId: station.ReaderId,
            ReaderName: reader?.Name ?? "Unknown station",
            ReaderOnline: online,
            ReaderSupportedHere: _rfid.AutoConnect,
            Mode: station.Mode,
            Stage: stage,
            StudentName: station.StudentName,
            RollNumber: station.RollNumber,
            Department: station.Department,
            StudentWarnings: [.. station.StudentWarnings],
            Items: station.Items.Values.OrderBy(i => i.AddedUtc).ToList(),
            Notices: [.. station.Notices],
            Receipt: station.Receipt,
            LoanDays: policy.DefaultLoanDays,
            Currency: policy.Currency,
            IdleSecondsRemaining: stage is KioskStage.Idle or KioskStage.Unavailable
                ? idleLimit
                : Math.Max(idleLimit - elapsed, 0),
            Version: station.Version);
    }

    /// <summary>
    /// Cover art for a copy. Two columns carry it in the inherited schema and neither is reliably
    /// populated, so both are tried before giving up.
    /// </summary>
    private static string? Cover(Domain.Entities.BookCopy? copy)
    {
        var url = copy?.Book?.ImageUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var fallback = copy?.Book?.BookImage;
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>An EPC is 24 characters; a kiosk screen only needs enough to identify the tag.</summary>
    private static string Shorten(string epc) =>
        epc.Length <= 10 ? epc : $"…{epc[^8..]}";
}
