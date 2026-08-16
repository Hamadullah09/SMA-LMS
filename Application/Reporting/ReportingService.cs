using System.Globalization;
using System.Text;
using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Application.Reporting;

public sealed record ReportRow(IReadOnlyList<string> Cells);

public sealed record ReportResult(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ReportRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;
}

public sealed record ReportDefinition(string Key, string Title, string Description);

/// <summary>
/// Library reports (specification section 54), all date-filtered and CSV-exportable.
///
/// Every report is a projection to strings so one CSV writer and one table view serve all of
/// them — adding a report means adding a query, not a view.
/// </summary>
public interface IReportingService
{
    IReadOnlyList<ReportDefinition> Available { get; }
    Task<ReportResult> RunAsync(string key, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    string ToCsv(ReportResult report);
}

public sealed class ReportingService : IReportingService
{
    private readonly ApplicationDbContext _db;

    public ReportingService(ApplicationDbContext db) => _db = db;

    public IReadOnlyList<ReportDefinition> Available { get; } =
    [
        new("circulation", "Circulation",
            "Every issue and return in the period, with method and operator."),
        new("most-borrowed", "Most borrowed titles",
            "Titles ranked by number of loans started in the period."),
        new("overdue", "Overdue books",
            "Loans still open past their due date, with the fine accrued so far."),
        new("fines", "Fine collection",
            "Fines raised in the period, paid and outstanding."),
        new("active-students", "Most active students",
            "Students ranked by loans taken in the period."),
        new("stock-condition", "Lost and damaged stock",
            "Copies withdrawn from circulation, with the note recorded at the time."),
        new("rfid-activity", "RFID activity",
            "Scan events per reader in the period."),
        new("manual-transactions", "Manual transactions",
            "Circulation performed without RFID — the fallback audit trail.")
    ];

    public async Task<ReportResult> RunAsync(
        string key, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // Inclusive of the whole end day: a report "to 15 August" must include 15 August.
        var from = fromUtc.Date;
        var to = toUtc.Date.AddDays(1).AddTicks(-1);

        var definition = Available.FirstOrDefault(d => d.Key == key) ?? Available[0];

        return definition.Key switch
        {
            "most-borrowed" => await MostBorrowedAsync(definition, from, to, ct),
            "overdue" => await OverdueAsync(definition, ct),
            "fines" => await FinesAsync(definition, from, to, ct),
            "active-students" => await ActiveStudentsAsync(definition, from, to, ct),
            "stock-condition" => await StockConditionAsync(definition, ct),
            "rfid-activity" => await RfidActivityAsync(definition, from, to, ct),
            "manual-transactions" => await ManualTransactionsAsync(definition, from, to, ct),
            _ => await CirculationAsync(definition, from, to, ct)
        };
    }

    private async Task<ReportResult> CirculationAsync(
        ReportDefinition d, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => (r.BorrowDate >= from && r.BorrowDate <= to)
                        || (r.ReturnDate != null && r.ReturnDate >= from && r.ReturnDate <= to))
            .OrderByDescending(r => r.BorrowDate)
            .Select(r => new
            {
                r.TransactionNumber,
                Student = r.Student == null ? "—" : r.Student.FullName,
                Title = r.Book!.Title,
                Copy = r.BookCopy == null ? "—" : r.BookCopy.CopyNumber,
                r.BorrowDate,
                r.DueDate,
                r.ReturnDate,
                r.IssueMethod,
                r.ReturnMethod
            })
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Transaction", "Student", "Title", "Copy", "Issued", "Due", "Returned", "Issue method", "Return method"],
            rows.Select(r => new ReportRow([
                r.TransactionNumber ?? "—",
                r.Student,
                r.Title,
                r.Copy,
                Date(r.BorrowDate),
                Date(r.DueDate),
                r.ReturnDate is null ? "Still out" : Date(r.ReturnDate.Value),
                r.IssueMethod.ToString(),
                r.ReturnMethod?.ToString() ?? "—"
            ])).ToList());
    }

    private async Task<ReportResult> MostBorrowedAsync(
        ReportDefinition d, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.BorrowDate >= from && r.BorrowDate <= to)
            .GroupBy(r => new { r.BookId, r.Book!.Title, r.Book.Author })
            .Select(g => new { g.Key.Title, g.Key.Author, Loans = g.Count() })
            .OrderByDescending(x => x.Loans)
            .Take(50)
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Title", "Author", "Loans"],
            rows.Select(r => new ReportRow([r.Title, r.Author, r.Loans.ToString()])).ToList());
    }

    private async Task<ReportResult> OverdueAsync(ReportDefinition d, CancellationToken ct)
    {
        // Overdue is a "right now" question, so this report ignores the date filter by design.
        var today = DateTime.UtcNow.Date;

        var rate = await _db.LibraryPolicies.AsNoTracking()
            .Where(p => p.Key == Domain.Entities.LibraryPolicy.Keys.FinePerDay)
            .Select(p => p.Value)
            .FirstOrDefaultAsync(ct);

        var perDay = decimal.TryParse(rate, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 20m;

        var rows = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.ReturnDate == null && r.DueDate < today)
            .OrderBy(r => r.DueDate)
            .Select(r => new
            {
                r.TransactionNumber,
                Student = r.Student == null ? "—" : r.Student.FullName,
                Roll = r.Student == null ? "—" : r.Student.RollNumber,
                Title = r.Book!.Title,
                r.DueDate
            })
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Transaction", "Student", "Roll number", "Title", "Due", "Days overdue", "Fine so far"],
            rows.Select(r =>
            {
                var days = (int)(today - r.DueDate.Date).TotalDays;
                return new ReportRow([
                    r.TransactionNumber ?? "—", r.Student, r.Roll, r.Title,
                    Date(r.DueDate), days.ToString(), (days * perDay).ToString("0.00", CultureInfo.InvariantCulture)
                ]);
            }).ToList());
    }

    private async Task<ReportResult> FinesAsync(
        ReportDefinition d, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.Fines
            .AsNoTracking()
            .Where(f => f.Borrowing != null
                        && f.Borrowing.ReturnDate != null
                        && f.Borrowing.ReturnDate >= from
                        && f.Borrowing.ReturnDate <= to)
            .OrderByDescending(f => f.Borrowing!.ReturnDate)
            .Select(f => new
            {
                f.Borrowing!.TransactionNumber,
                Student = f.Borrowing.Student == null ? "—" : f.Borrowing.Student.FullName,
                Title = f.Borrowing.Book!.Title,
                f.Amount,
                f.Paid,
                f.Borrowing.ReturnDate,
                f.Remark
            })
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Transaction", "Student", "Title", "Returned", "Amount", "Status", "Note"],
            rows.Select(r => new ReportRow([
                r.TransactionNumber ?? "—", r.Student, r.Title,
                r.ReturnDate is null ? "—" : Date(r.ReturnDate.Value),
                r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                r.Paid ? "Paid" : "Outstanding",
                r.Remark ?? ""
            ])).ToList());
    }

    private async Task<ReportResult> ActiveStudentsAsync(
        ReportDefinition d, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.BorrowDate >= from && r.BorrowDate <= to && r.StudentId != null)
            .GroupBy(r => new { r.StudentId, r.Student!.FullName, r.Student.RollNumber })
            .Select(g => new { g.Key.FullName, g.Key.RollNumber, Loans = g.Count() })
            .OrderByDescending(x => x.Loans)
            .Take(50)
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Student", "Roll number", "Loans"],
            rows.Select(r => new ReportRow([r.FullName, r.RollNumber, r.Loans.ToString()])).ToList());
    }

    private async Task<ReportResult> StockConditionAsync(ReportDefinition d, CancellationToken ct)
    {
        var rows = await _db.BookCopies
            .AsNoTracking()
            .Where(c => c.Status == BookCopyStatus.Lost
                        || c.Status == BookCopyStatus.Damaged
                        || c.Status == BookCopyStatus.Missing
                        || c.Status == BookCopyStatus.UnderMaintenance)
            .OrderBy(c => c.Book!.Title)
            .Select(c => new
            {
                Title = c.Book!.Title,
                c.CopyNumber,
                c.AccessionNumber,
                c.Status,
                c.StatusNote,
                c.StatusChangedUtc,
                c.StatusChangedBy
            })
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Title", "Copy", "Accession", "Status", "Recorded", "By", "Note"],
            rows.Select(r => new ReportRow([
                r.Title, r.CopyNumber, r.AccessionNumber ?? "—", r.Status.ToString(),
                r.StatusChangedUtc is null ? "—" : Date(r.StatusChangedUtc.Value),
                r.StatusChangedBy ?? "—", r.StatusNote ?? ""
            ])).ToList());
    }

    private async Task<ReportResult> RfidActivityAsync(
        ReportDefinition d, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.RfidScanEvents
            .AsNoTracking()
            .Where(e => e.LastObservedUtc >= from && e.LastObservedUtc <= to)
            .GroupBy(e => new { e.ReaderId, e.Reader!.Name })
            .Select(g => new
            {
                g.Key.Name,
                Scans = g.Count(),
                Reads = g.Sum(x => x.ReadCount),
                Unknown = g.Count(x => x.ResolvedKind == null),
                Last = g.Max(x => x.LastObservedUtc)
            })
            .OrderByDescending(x => x.Scans)
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Reader", "Logical scans", "Raw reads", "Unknown tags", "Last activity"],
            rows.Select(r => new ReportRow([
                r.Name, r.Scans.ToString(), r.Reads.ToString(), r.Unknown.ToString(), Date(r.Last)
            ])).ToList());
    }

    private async Task<ReportResult> ManualTransactionsAsync(
        ReportDefinition d, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.BorrowingRecords
            .AsNoTracking()
            .Where(r => r.BorrowDate >= from && r.BorrowDate <= to
                        && (r.IssueMethod == CirculationMethod.Manual
                            || r.ReturnMethod == CirculationMethod.Manual))
            .OrderByDescending(r => r.BorrowDate)
            .Select(r => new
            {
                r.TransactionNumber,
                Student = r.Student == null ? "—" : r.Student.FullName,
                Title = r.Book!.Title,
                r.BorrowDate,
                r.IssueMethod,
                r.ReturnMethod,
                r.CreatedBy,
                r.ReturnUserId
            })
            .ToListAsync(ct);

        return new ReportResult(d.Key, d.Title, d.Description,
            ["Transaction", "Student", "Title", "Issued", "Issue method", "Return method", "Issued by", "Returned by"],
            rows.Select(r => new ReportRow([
                r.TransactionNumber ?? "—", r.Student, r.Title, Date(r.BorrowDate),
                r.IssueMethod.ToString(), r.ReturnMethod?.ToString() ?? "—",
                r.CreatedBy ?? "—", r.ReturnUserId ?? "—"
            ])).ToList());
    }

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// RFC 4180 CSV. Fields are quoted whenever they contain a delimiter, quote or newline, and
    /// a leading =, +, - or @ is prefixed with an apostrophe so spreadsheet software does not
    /// interpret library data as a formula.
    /// </summary>
    public string ToCsv(ReportResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', report.Columns.Select(Escape)));

        foreach (var row in report.Rows)
        {
            builder.AppendLine(string.Join(',', row.Cells.Select(Escape)));
        }

        return builder.ToString();
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@'))
        {
            text = "'" + text;
        }

        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            text = "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }
}
