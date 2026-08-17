using System.Text;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Rfid;

/// <summary>How the importer decided to spread stock codes across titles.</summary>
public enum TagImportDistribution
{
    /// <summary>
    /// Consecutive runs of stock codes go to the same title, the way a library that bought fifteen
    /// copies at once would accession them. Keeps a shelf run together.
    /// </summary>
    ContiguousBlocks = 0,

    /// <summary>One stock code per title in rotation, scattering each title across the ranges.</summary>
    RoundRobin = 1
}

/// <summary>What the importer will do, or did, with one row of the file.</summary>
public enum TagImportAction
{
    /// <summary>No copy carried this stock code, so both the copy and its tag are new.</summary>
    CreateCopyAndAttachTag = 0,

    /// <summary>The copy already existed by accession number; only the tag is new.</summary>
    AttachTagToExistingCopy = 1,

    /// <summary>This exact EPC is already the active tag on this exact copy. Nothing to do.</summary>
    AlreadyCorrect = 2,

    /// <summary>The EPC is live against a different copy or a student card, so it is left alone.</summary>
    ConflictTagInUse = 3,

    /// <summary>The row could not be read as an EPC and stock code pair.</summary>
    InvalidRow = 4
}

public sealed record TagImportItem(
    int LineNumber,
    string StockCode,
    string Epc,
    TagImportAction Action,
    string? BookTitle,
    string? CopyNumber,
    string? Note)
{
    public bool IsProblem => Action is TagImportAction.ConflictTagInUse or TagImportAction.InvalidRow;
}

public sealed record TagImportReport(
    bool DryRun,
    int TotalRows,
    int CopiesCreated,
    int TagsAttached,
    int AlreadyCorrect,
    int Conflicts,
    int InvalidRows,
    int TitlesTouched,
    IReadOnlyList<TagImportItem> Items)
{
    public int Applied => CopiesCreated + TagsAttached;

    public IEnumerable<TagImportItem> Problems => Items.Where(i => i.IsProblem);

    public string Summary => DryRun
        ? $"Preview only — nothing saved. {CopiesCreated} copy/copies would be created, "
          + $"{TagsAttached} tag(s) attached across {TitlesTouched} title(s). "
          + $"{AlreadyCorrect} already correct, {Conflicts} conflict(s), {InvalidRows} unreadable row(s)."
        : $"Imported. {CopiesCreated} copy/copies created and {TagsAttached} tag(s) attached across "
          + $"{TitlesTouched} title(s). {AlreadyCorrect} already correct, {Conflicts} conflict(s), "
          + $"{InvalidRows} unreadable row(s).";
}

/// <summary>What the importer will do, or did, with one line of a student-card label sheet.</summary>
public enum StudentCardImportAction
{
    /// <summary>The card was issued to a student who had none.</summary>
    Assigned = 0,

    /// <summary>This exact EPC is already the active card for the student it would be given to.</summary>
    AlreadyCorrect = 1,

    /// <summary>The EPC is live against another student or a book, so it is left alone.</summary>
    ConflictTagInUse = 2,

    /// <summary>
    /// There are more cards on the sheet than students without one. Not an error — a spare card
    /// waiting for somebody to enrol.
    /// </summary>
    NoStudentAvailable = 3,

    InvalidRow = 4
}

public sealed record StudentCardImportItem(
    int LineNumber,
    string? LabelId,
    string Epc,
    StudentCardImportAction Action,
    string? StudentName,
    string? RollNumber,
    string? Note)
{
    public bool IsProblem =>
        Action is StudentCardImportAction.ConflictTagInUse or StudentCardImportAction.InvalidRow;
}

public sealed record StudentCardImportReport(
    bool DryRun,
    int TotalRows,
    int Assigned,
    int AlreadyCorrect,
    int Conflicts,
    int InvalidRows,
    int SpareCards,
    IReadOnlyList<StudentCardImportItem> Items)
{
    public IEnumerable<StudentCardImportItem> Problems => Items.Where(i => i.IsProblem);

    public string Summary
    {
        get
        {
            var head = DryRun
                ? $"Preview only — nothing saved. {Assigned} card(s) would be issued."
                : $"Done. {Assigned} card(s) issued.";

            var tail = $" {AlreadyCorrect} already correct, {Conflicts} conflict(s), "
                       + $"{InvalidRows} unreadable row(s).";

            var spare = SpareCards > 0
                ? $" {SpareCards} card(s) have no student left to issue to — they stay unassigned "
                  + "until a student needs one."
                : string.Empty;

            return head + tail + spare;
        }
    }
}

public sealed record TagImportOptions(
    TagImportDistribution Distribution = TagImportDistribution.ContiguousBlocks,
    bool DryRun = true,
    bool SyncBookQuantity = true);

/// <summary>
/// Bulk-attaches manufacturer-supplied RFID tags to the catalogue.
///
/// The file the library gets from its tag supplier is a flat list of EPC against stock code, and
/// nothing in it says which title a stock code belongs to — so the importer has to decide, and it
/// does so deterministically from the sorted stock codes rather than from database order. That
/// matters for re-runs: the same file always produces the same mapping, so importing twice is safe
/// and importing a corrected file does not shuffle everything that already worked.
///
/// Two rules are inherited from <see cref="IRfidTagService"/> rather than reinvented, because a bulk
/// path that is more permissive than the single-tag path would be a hole in exactly the checks that
/// matter (specification sections 4F, 87):
///   * a live EPC belongs to exactly one entity — a conflict is reported, never silently reassigned
///   * nothing is deleted — an existing copy keeps its identity and its history
/// </summary>
public interface IRfidTagImportService
{
    /// <summary>The tag file shipped with the application, if present.</summary>
    Task<string?> ReadBundledFileAsync(CancellationToken ct = default);

    Task<TagImportReport> ImportAsync(
        string csv, TagImportOptions options, string? actor, CancellationToken ct = default);

    /// <summary>The student-card label sheet shipped with the application, if present.</summary>
    Task<string?> ReadBundledStudentCardFileAsync(CancellationToken ct = default);

    /// <summary>
    /// Issues cards from a label sheet to students who do not have one.
    ///
    /// The sheet carries no student identity — only a sequence number and an EPC — so the pairing is
    /// this importer's decision. It issues to students in roll-number order, which is deterministic
    /// and therefore safe to re-run, and it never takes a card off somebody who already has one.
    /// A librarian can move any individual card afterwards on the tag assignment screen.
    /// </summary>
    Task<StudentCardImportReport> ImportStudentCardsAsync(
        string text, bool dryRun, string? actor, CancellationToken ct = default);
}

public sealed class RfidTagImportService : IRfidTagImportService
{
    /// <summary>Path relative to the content root. Not under wwwroot: it is not a public asset.</summary>
    public const string BundledFileRelativePath = "Data/Seed/rfid-book-tags.csv";

    /// <summary>Student-card label sheet, same content-root convention as the book manifest.</summary>
    public const string BundledStudentCardFileRelativePath = "Data/Seed/rfid-student-cards.txt";

    private readonly ApplicationDbContext _db;
    private readonly IRfidTagService _tags;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<RfidTagImportService> _logger;

    public RfidTagImportService(
        ApplicationDbContext db,
        IRfidTagService tags,
        IWebHostEnvironment environment,
        ILogger<RfidTagImportService> logger)
    {
        _db = db;
        _tags = tags;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string?> ReadBundledFileAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_environment.ContentRootPath, BundledFileRelativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public async Task<string?> ReadBundledStudentCardFileAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_environment.ContentRootPath, BundledStudentCardFileRelativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    // ------------------------------------------------------------------ parsing

    private sealed record ParsedRow(int LineNumber, string StockCode, string Epc, string? Error);

    /// <summary>
    /// Reads the supplier's two-column file.
    ///
    /// EPCs arrive inconsistently formatted — some rows space-separated per byte, some not — so
    /// whitespace is stripped and the result upper-cased. That is not cosmetic: the reader reports
    /// an EPC as contiguous uppercase hex (<c>Convert.ToHexString</c>), and a stored value with
    /// spaces in it would never match a scan and the tag would read as unknown forever.
    /// </summary>
    private static List<ParsedRow> Parse(string csv)
    {
        var rows = new List<ParsedRow>();
        var lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');

            // Header row, in whichever case the supplier used.
            if (i == 0 && fields[0].Trim().Equals("EPC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Length < 2)
            {
                rows.Add(new ParsedRow(lineNumber, line, string.Empty,
                    "Expected two comma-separated columns: EPC then stock code."));
                continue;
            }

            var epc = new string(fields[0].Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            var stockCode = fields[1].Trim().ToUpperInvariant();

            if (epc.Length == 0 || stockCode.Length == 0)
            {
                rows.Add(new ParsedRow(lineNumber, stockCode, epc, "EPC or stock code is blank."));
                continue;
            }

            if (!epc.All(Uri.IsHexDigit))
            {
                rows.Add(new ParsedRow(lineNumber, stockCode, epc, "EPC is not hexadecimal."));
                continue;
            }

            // Hex must come in whole bytes. An odd count means a digit was lost in transcription,
            // and half a byte would never match a real read.
            if (epc.Length % 2 != 0)
            {
                rows.Add(new ParsedRow(lineNumber, stockCode, epc,
                    $"EPC has {epc.Length} hex digits, which is not a whole number of bytes."));
                continue;
            }

            if (stockCode.Length > 60)
            {
                rows.Add(new ParsedRow(lineNumber, stockCode, epc,
                    "Stock code is longer than the 60 characters an accession number allows."));
                continue;
            }

            rows.Add(new ParsedRow(lineNumber, stockCode, epc, null));
        }

        return rows;
    }

    // ------------------------------------------------------------------ import

    public async Task<TagImportReport> ImportAsync(
        string csv, TagImportOptions options, string? actor, CancellationToken ct = default)
    {
        var parsed = Parse(csv);
        var items = new List<TagImportItem>();

        foreach (var bad in parsed.Where(r => r.Error is not null))
        {
            items.Add(new TagImportItem(
                bad.LineNumber, bad.StockCode, bad.Epc, TagImportAction.InvalidRow,
                null, null, bad.Error));
        }

        // A stock code repeated in the file is a supplier error we must not act on twice: the first
        // occurrence wins and the rest are reported.
        var seenStock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEpc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usable = new List<ParsedRow>();

        foreach (var row in parsed.Where(r => r.Error is null).OrderBy(r => r.StockCode, StringComparer.Ordinal))
        {
            if (!seenStock.Add(row.StockCode))
            {
                items.Add(new TagImportItem(
                    row.LineNumber, row.StockCode, row.Epc, TagImportAction.InvalidRow,
                    null, null, "This stock code appears more than once in the file."));
                continue;
            }

            if (!seenEpc.Add(row.Epc))
            {
                items.Add(new TagImportItem(
                    row.LineNumber, row.StockCode, row.Epc, TagImportAction.InvalidRow,
                    null, null, "This EPC appears more than once in the file."));
                continue;
            }

            usable.Add(row);
        }

        var books = await _db.Books
            .AsNoTracking()
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Title })
            .ToListAsync(ct);

        if (books.Count == 0)
        {
            return new TagImportReport(
                options.DryRun, parsed.Count, 0, 0, 0, 0, items.Count, 0,
                [.. items, new TagImportItem(0, string.Empty, string.Empty, TagImportAction.InvalidRow,
                    null, null, "There are no books in the catalogue to attach copies to.")]);
        }

        // Existing state, loaded once. Per-row queries would be 200 round trips for a 200-row file.
        var stockCodes = usable.Select(r => r.StockCode).ToList();
        var epcs = usable.Select(r => r.Epc).ToList();

        var existingCopies = await _db.BookCopies
            .Where(c => c.AccessionNumber != null && stockCodes.Contains(c.AccessionNumber))
            .ToDictionaryAsync(c => c.AccessionNumber!, StringComparer.OrdinalIgnoreCase, ct);

        var liveBookTags = await _db.BookRfidTags
            .Where(t => t.IsActive && epcs.Contains(t.Epc))
            .Include(t => t.BookCopy).ThenInclude(c => c!.Book)
            .ToDictionaryAsync(t => t.Epc, StringComparer.OrdinalIgnoreCase, ct);

        var liveStudentTags = await _db.StudentRfidTags
            .Where(t => t.IsActive && epcs.Contains(t.Epc))
            .Include(t => t.Student)
            .ToDictionaryAsync(t => t.Epc, StringComparer.OrdinalIgnoreCase, ct);

        // Next free copy number per title, so an import onto a title that already holds copies
        // continues the sequence instead of colliding with it.
        var nextCopyNumber = await _db.BookCopies
            .Where(c => c.CopyNumber != "LEGACY")
            .GroupBy(c => c.BookId)
            .Select(g => new { BookId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BookId, x => x.Count + 1, ct);

        var assignment = Distribute(usable.Count, books.Count, options.Distribution);

        var now = DateTime.UtcNow;
        var created = 0;
        var attached = 0;
        var alreadyCorrect = 0;
        var conflicts = 0;
        var titlesTouched = new HashSet<int>();

        for (var index = 0; index < usable.Count; index++)
        {
            var row = usable[index];

            // ---- the EPC must not already be live somewhere else (§4F) ----
            if (liveStudentTags.TryGetValue(row.Epc, out var studentTag))
            {
                conflicts++;
                items.Add(new TagImportItem(
                    row.LineNumber, row.StockCode, row.Epc, TagImportAction.ConflictTagInUse,
                    null, null,
                    $"This EPC is the active student card for {studentTag.Student?.FullName ?? "a student"}. "
                    + "Revoke that card first if the tag really belongs on a book."));
                continue;
            }

            existingCopies.TryGetValue(row.StockCode, out var copy);

            if (liveBookTags.TryGetValue(row.Epc, out var bookTag))
            {
                if (copy is not null && bookTag.BookCopyId == copy.Id)
                {
                    alreadyCorrect++;
                    items.Add(new TagImportItem(
                        row.LineNumber, row.StockCode, row.Epc, TagImportAction.AlreadyCorrect,
                        bookTag.BookCopy?.Book?.Title, copy.CopyNumber,
                        "Already attached to this copy."));
                    continue;
                }

                conflicts++;
                items.Add(new TagImportItem(
                    row.LineNumber, row.StockCode, row.Epc, TagImportAction.ConflictTagInUse,
                    bookTag.BookCopy?.Book?.Title, bookTag.BookCopy?.CopyNumber,
                    "This EPC is already attached to a different copy. Detach it there first."));
                continue;
            }

            // ---- resolve or create the copy ----
            string bookTitle;
            string copyNumber;

            if (copy is not null)
            {
                // The accession number already identifies a physical item. Keep it where it is —
                // moving a copy between titles would rewrite catalogue history to fit a file.
                bookTitle = books.FirstOrDefault(b => b.Id == copy.BookId)?.Title ?? "Unknown title";
                copyNumber = copy.CopyNumber;
                titlesTouched.Add(copy.BookId);

                if (!options.DryRun)
                {
                    AttachTag(copy.Id, row.Epc, actor, now);
                }

                attached++;
                items.Add(new TagImportItem(
                    row.LineNumber, row.StockCode, row.Epc, TagImportAction.AttachTagToExistingCopy,
                    bookTitle, copyNumber, "Copy already existed under this accession number."));
                continue;
            }

            var book = books[assignment[index]];
            bookTitle = book.Title;
            titlesTouched.Add(book.Id);

            var sequence = nextCopyNumber.TryGetValue(book.Id, out var next) ? next : 1;
            nextCopyNumber[book.Id] = sequence + 1;
            copyNumber = sequence.ToString("D3");

            if (!options.DryRun)
            {
                var newCopy = new BookCopy
                {
                    BookId = book.Id,
                    CopyNumber = copyNumber,
                    AccessionNumber = row.StockCode,
                    Status = BookCopyStatus.Available,
                    Condition = BookCondition.Good,
                    AcquisitionSource = "RFID tag import",
                    CreatedBy = actor,
                    CreatedUtc = now
                };

                _db.BookCopies.Add(newCopy);

                // The tag needs the copy's identity, so the copy is saved first. One save per row
                // is the cost of using the generated key; 200 of them is still fast, and the
                // alternative is tracking navigation graphs by hand for no real gain.
                await _db.SaveChangesAsync(ct);

                AttachTag(newCopy.Id, row.Epc, actor, now);
                await _db.SaveChangesAsync(ct);
            }

            created++;
            attached++;
            items.Add(new TagImportItem(
                row.LineNumber, row.StockCode, row.Epc, TagImportAction.CreateCopyAndAttachTag,
                bookTitle, copyNumber, null));
        }

        if (!options.DryRun)
        {
            await _db.SaveChangesAsync(ct);

            if (options.SyncBookQuantity)
            {
                await SyncBookQuantitiesAsync(titlesTouched, ct);
            }

            _logger.LogInformation(
                "RFID tag import by {Actor}: {Created} copies created, {Attached} tags attached, "
                + "{Conflicts} conflicts, {Invalid} unreadable rows.",
                actor ?? "unknown", created, attached, conflicts, items.Count(i => i.Action == TagImportAction.InvalidRow));
        }

        return new TagImportReport(
            options.DryRun,
            parsed.Count,
            created,
            attached,
            alreadyCorrect,
            conflicts,
            items.Count(i => i.Action == TagImportAction.InvalidRow),
            titlesTouched.Count,
            items.OrderBy(i => i.LineNumber).ToList());
    }

    private void AttachTag(int bookCopyId, string epc, string? actor, DateTime now)
    {
        _db.BookRfidTags.Add(new BookRfidTag
        {
            BookCopyId = bookCopyId,
            Epc = epc,
            State = RfidTagState.Active,
            IsActive = true,
            AssignedBy = actor,
            AssignedUtc = now
        });

        _db.AuditLogs.Add(new AuditLog
        {
            Operation = "RfidImport",
            EntityType = "BookCopy",
            EntityId = bookCopyId.ToString(),
            RfidEpc = epc,
            UserName = actor,
            NewValue = "Tag attached by bulk import",
            Succeeded = true,
            OccurredUtc = now
        });
    }

    /// <summary>
    /// Keeps the legacy scalar <c>Book.Quantity</c> in step with the copies that now exist.
    ///
    /// The student-facing catalogue counts real copies, but several inherited admin screens still
    /// read Quantity. Leaving it at its seeded value would show one number to students and another
    /// to staff for the same title.
    /// </summary>
    private async Task SyncBookQuantitiesAsync(IReadOnlyCollection<int> bookIds, CancellationToken ct)
    {
        if (bookIds.Count == 0)
        {
            return;
        }

        var counts = await _db.BookCopies
            .Where(c => bookIds.Contains(c.BookId) && c.CopyNumber != "LEGACY")
            .GroupBy(c => c.BookId)
            .Select(g => new
            {
                BookId = g.Key,
                Total = g.Count(),
                Available = g.Count(c => c.Status == BookCopyStatus.Available)
            })
            .ToListAsync(ct);

        var books = await _db.Books.Where(b => bookIds.Contains(b.Id)).ToListAsync(ct);

        foreach (var book in books)
        {
            var count = counts.FirstOrDefault(c => c.BookId == book.Id);
            if (count is null)
            {
                continue;
            }

            book.Quantity = count.Total;
            book.Availability = count.Available > 0;
        }

        await _db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ student cards

    /// <summary>
    /// A 96-bit EPC is 12 bytes, so 24 hex characters. The label sheet prefixes each row with a
    /// sequence number, and columns are separated inconsistently by tabs and spaces — so rather than
    /// trying to split columns, whitespace is stripped and the EPC is taken as the trailing 24
    /// characters. Anything in front of that is the label's own row number.
    /// </summary>
    private const int EpcHexLength = 24;

    private static (string? LabelId, string Epc, string? Error) ParseCardLine(string line)
    {
        var packed = new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (packed.Length == 0)
        {
            return (null, string.Empty, "Blank line.");
        }

        if (packed.Length < EpcHexLength)
        {
            return (null, packed.ToUpperInvariant(),
                $"Expected a {EpcHexLength}-character EPC; found only {packed.Length} characters.");
        }

        var epc = packed[^EpcHexLength..].ToUpperInvariant();
        var prefix = packed[..^EpcHexLength];

        if (!epc.All(Uri.IsHexDigit))
        {
            return (null, epc, "EPC is not hexadecimal.");
        }

        // Anything before the EPC must look like a row number. If it does not, the line is probably
        // a header or a longer identifier, and guessing which 24 characters are the EPC would be
        // worse than refusing.
        if (prefix.Length > 0 && !prefix.All(char.IsAsciiDigit))
        {
            return (null, epc, $"Unexpected text before the EPC: \"{prefix}\".");
        }

        return (prefix.Length > 0 ? prefix : null, epc, null);
    }

    public async Task<StudentCardImportReport> ImportStudentCardsAsync(
        string text, bool dryRun, string? actor, CancellationToken ct = default)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var items = new List<StudentCardImportItem>();
        var usable = new List<(int Line, string? LabelId, string Epc)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var raw = lines[i];

            // Header row, whatever spacing it uses.
            if (i == 0 && raw.Replace("\t", " ").Trim().StartsWith("ID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (labelId, epc, error) = ParseCardLine(raw);

            if (error is not null)
            {
                items.Add(new StudentCardImportItem(
                    lineNumber, labelId, epc, StudentCardImportAction.InvalidRow, null, null, error));
                continue;
            }

            if (!seen.Add(epc))
            {
                items.Add(new StudentCardImportItem(
                    lineNumber, labelId, epc, StudentCardImportAction.InvalidRow, null, null,
                    "This EPC appears more than once on the sheet."));
                continue;
            }

            usable.Add((lineNumber, labelId, epc));
        }

        // Students without a live card, in roll-number order so the pairing is reproducible.
        var candidates = await _db.Students
            .AsNoTracking()
            .Where(s => s.Status == StudentStatus.Active
                        && !_db.StudentRfidTags.Any(t => t.StudentId == s.Id && t.IsActive))
            .OrderBy(s => s.RollNumber)
            .Select(s => new { s.Id, s.FullName, s.RollNumber })
            .ToListAsync(ct);

        var epcs = usable.Select(u => u.Epc).ToList();

        var liveStudentTags = await _db.StudentRfidTags
            .AsNoTracking()
            .Where(t => t.IsActive && epcs.Contains(t.Epc))
            .Include(t => t.Student)
            .ToDictionaryAsync(t => t.Epc, StringComparer.OrdinalIgnoreCase, ct);

        var liveBookTags = await _db.BookRfidTags
            .AsNoTracking()
            .Where(t => t.IsActive && epcs.Contains(t.Epc))
            .Include(t => t.BookCopy).ThenInclude(c => c!.Book)
            .ToDictionaryAsync(t => t.Epc, StringComparer.OrdinalIgnoreCase, ct);

        var assigned = 0;
        var alreadyCorrect = 0;
        var conflicts = 0;
        var spare = 0;
        var next = 0;

        foreach (var (line, labelId, epc) in usable)
        {
            // ---- the EPC must not already be live somewhere (§4F) ----
            if (liveBookTags.TryGetValue(epc, out var bookTag))
            {
                conflicts++;
                items.Add(new StudentCardImportItem(
                    line, labelId, epc, StudentCardImportAction.ConflictTagInUse, null, null,
                    $"This EPC is attached to \"{bookTag.BookCopy?.Book?.Title}\" copy "
                    + $"{bookTag.BookCopy?.CopyNumber}. Detach it there before using it as a card."));
                continue;
            }

            if (liveStudentTags.TryGetValue(epc, out var studentTag))
            {
                alreadyCorrect++;
                items.Add(new StudentCardImportItem(
                    line, labelId, epc, StudentCardImportAction.AlreadyCorrect,
                    studentTag.Student?.FullName, studentTag.Student?.RollNumber,
                    "Already issued to this student."));
                continue;
            }

            if (next >= candidates.Count)
            {
                spare++;
                items.Add(new StudentCardImportItem(
                    line, labelId, epc, StudentCardImportAction.NoStudentAvailable, null, null,
                    "No student is waiting for a card. Assign it on the tag screen when one is."));
                continue;
            }

            var student = candidates[next++];

            if (!dryRun)
            {
                // Same service the single-card screen uses, so the uniqueness rule, the replacement
                // history and the audit entry are identical rather than reimplemented here.
                var result = await _tags.AssignStudentCardAsync(student.Id, epc, actor, ct);

                if (!result.Succeeded)
                {
                    conflicts++;
                    items.Add(new StudentCardImportItem(
                        line, labelId, epc, StudentCardImportAction.ConflictTagInUse,
                        student.FullName, student.RollNumber, result.Message));
                    continue;
                }
            }

            assigned++;
            items.Add(new StudentCardImportItem(
                line, labelId, epc, StudentCardImportAction.Assigned,
                student.FullName, student.RollNumber, null));
        }

        if (!dryRun)
        {
            _logger.LogInformation(
                "Student card import by {Actor}: {Assigned} issued, {Conflicts} conflict(s), "
                + "{Spare} spare, {Invalid} unreadable.",
                actor ?? "unknown", assigned, conflicts, spare,
                items.Count(i => i.Action == StudentCardImportAction.InvalidRow));
        }

        return new StudentCardImportReport(
            dryRun,
            items.Count(i => i.Action == StudentCardImportAction.InvalidRow) + usable.Count,
            assigned,
            alreadyCorrect,
            conflicts,
            items.Count(i => i.Action == StudentCardImportAction.InvalidRow),
            spare,
            items.OrderBy(i => i.LineNumber).ToList());
    }

    // ------------------------------------------------------------------ distribution

    /// <summary>
    /// Maps each row index to a book index.
    ///
    /// Contiguous mode spreads the remainder over the earliest titles rather than dumping it on the
    /// last one, so 200 codes over 14 titles gives four titles of 15 and ten of 14 — not thirteen of
    /// 15 and one of 5.
    /// </summary>
    internal static int[] Distribute(int rowCount, int bookCount, TagImportDistribution mode)
    {
        var assignment = new int[rowCount];

        if (rowCount == 0 || bookCount == 0)
        {
            return assignment;
        }

        if (mode == TagImportDistribution.RoundRobin)
        {
            for (var i = 0; i < rowCount; i++)
            {
                assignment[i] = i % bookCount;
            }

            return assignment;
        }

        var baseSize = rowCount / bookCount;
        var remainder = rowCount % bookCount;

        var cursor = 0;
        for (var book = 0; book < bookCount && cursor < rowCount; book++)
        {
            var size = baseSize + (book < remainder ? 1 : 0);

            for (var n = 0; n < size && cursor < rowCount; n++)
            {
                assignment[cursor++] = book;
            }
        }

        // If there are more books than rows the tail is already zero-filled but unused, because the
        // loop above never reaches those books.
        return assignment;
    }
}
