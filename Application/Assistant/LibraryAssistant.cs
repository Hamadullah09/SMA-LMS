using System.Text.RegularExpressions;
using Library_Management_system.Application.Policies;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Application.Assistant;

public sealed record AssistantAnswer(
    string Text,
    string Intent,
    IReadOnlyList<AssistantLink> Links,
    bool RequiresSignIn = false)
{
    public static AssistantAnswer Say(string intent, string text, params AssistantLink[] links) =>
        new(text, intent, links);
}

public sealed record AssistantLink(string Label, string Url);

/// <summary>
/// The library assistant (specification sections 12, 13).
///
/// DESIGN NOTE — this is deliberately NOT a language model.
///
/// Section 13 requires a *controlled* assistant that answers only from application services and
/// "must not invent book availability or locations". A generative model would introduce exactly
/// the failure it forbids, plus an external network dependency that section 2's MyASP.NET
/// constraints rule out.
///
/// So this is an intent router over the same read-only services the rest of the application uses.
/// Every number it states — copies available, days remaining, fine owed — is read from the
/// database at the moment of asking. When it cannot answer, it says so rather than guessing.
///
/// If a language model is wanted later, the correct shape is to keep these methods as the tool
/// surface and let the model choose between them. The answers must still come from here.
/// </summary>
public interface ILibraryAssistant
{
    Task<AssistantAnswer> AskAsync(string question, int? studentId, CancellationToken ct = default);
}

public sealed class LibraryAssistant : ILibraryAssistant
{
    private readonly ApplicationDbContext _db;
    private readonly ILibraryPolicyService _policies;
    private readonly LibraryHoursOptions _hours;

    public LibraryAssistant(
        ApplicationDbContext db,
        ILibraryPolicyService policies,
        IOptions<LibraryHoursOptions> hours)
    {
        _db = db;
        _policies = policies;
        _hours = hours.Value;
    }

    public async Task<AssistantAnswer> AskAsync(string question, int? studentId, CancellationToken ct = default)
    {
        var text = (question ?? string.Empty).Trim();
        if (text.Length < 2)
        {
            return AssistantAnswer.Say("empty",
                "Ask me about a book, where to find it, or your own loans and fines.");
        }

        var lower = text.ToLowerInvariant();

        // Account questions are answered only for the signed-in student, from their own data
        // (specification sections 12, 43).
        if (MentionsOwnAccount(lower))
        {
            return studentId is null
                ? new AssistantAnswer(
                    "I can only look up your loans and fines once you are signed in and your student "
                    + "record is linked. A librarian can link it for you.",
                    "account", [new AssistantLink("Sign in", "/Identity/Account/Login")], RequiresSignIn: true)
                : await AnswerAccountAsync(lower, studentId.Value, ct);
        }

        // Opening hours were the last fact the widget served from a hard-coded string, and that
        // string disagreed with the hours printed on the home and About pages. Answered here now.
        if (MentionsHours(lower))
        {
            return AnswerHours();
        }

        if (MentionsPolicy(lower))
        {
            return await AnswerPolicyAsync(lower, ct);
        }

        // Everything else is treated as a catalogue question.
        return await AnswerCatalogueAsync(text, lower, ct);
    }

    // ------------------------------------------------------------------ intents

    // Note the deliberate lack of a trailing \b on the subject words: "books", "loans", "fines"
    // and "reservations" are how students actually phrase these, and \bbook\b matches none of them.
    private static bool MentionsOwnAccount(string q) =>
        Regex.IsMatch(q, @"\b(my|i owe|do i|am i|when do i|what do i)\b")
        && Regex.IsMatch(q, @"\b(book|loan|borrow|fine|owe|due|reserv|hold|account)");

    // "open" and "close" are ordinary English words that turn up in titles ("The Closing of
    // the American Mind"), so they only count as an hours question when the sentence is not
    // asking for a book. The explicit hours vocabulary needs no such guard.
    private static bool MentionsHours(string q) =>
        !Regex.IsMatch(q, @"\b(do you have|where is|have you got|looking for|find me|is there a copy)\b")
        && Regex.IsMatch(q, @"\b(hours|timing|timings|what time|open|opening|close|closing|closed)\b");

    private static bool MentionsPolicy(string q) =>
        Regex.IsMatch(q, @"\b(how many|how long|policy|rule|allowed|maximum|late|overdue charge|fine per)\b");

    // ------------------------------------------------------------------ account

    private async Task<AssistantAnswer> AnswerAccountAsync(string q, int studentId, CancellationToken ct)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);

        if (q.Contains("fine") || q.Contains("owe"))
        {
            var owed = await _db.Fines.AsNoTracking()
                .Where(f => !f.Paid && f.Borrowing != null && f.Borrowing.StudentId == studentId)
                .SumAsync(f => (decimal?)f.Amount, ct) ?? 0m;

            return AssistantAnswer.Say("account.fines",
                owed == 0
                    ? "You have no outstanding fines."
                    : $"You owe {policy.Currency} {owed:0.00}. You can settle it at the circulation desk.",
                new AssistantLink("My Library", "/portal"));
        }

        if (q.Contains("reserv") || q.Contains("hold"))
        {
            var holds = await _db.Reservations.AsNoTracking()
                .Where(r => r.StudentId == studentId
                            && (r.Status == ReservationStatus.Queued || r.Status == ReservationStatus.Available))
                .Select(r => new { r.Book!.Title, r.QueuePosition, r.Status })
                .ToListAsync(ct);

            if (holds.Count == 0)
            {
                return AssistantAnswer.Say("account.reservations", "You have no reservations at the moment.");
            }

            var lines = holds.Select(h => h.Status == ReservationStatus.Available
                ? $"{h.Title} — ready to collect"
                : $"{h.Title} — number {h.QueuePosition} in the queue");

            return AssistantAnswer.Say("account.reservations",
                "You are waiting for:\n" + string.Join("\n", lines),
                new AssistantLink("My Library", "/portal"));
        }

        // Default account answer: what is out and when it is due.
        var loans = await _db.BorrowingRecords.AsNoTracking()
            .Where(r => r.StudentId == studentId && r.ReturnDate == null)
            .Select(r => new { r.Book!.Title, r.DueDate })
            .OrderBy(r => r.DueDate)
            .ToListAsync(ct);

        if (loans.Count == 0)
        {
            return AssistantAnswer.Say("account.loans",
                $"You have no books out. You may borrow up to {policy.MaximumBooksPerStudent} at a time.",
                new AssistantLink("Browse the catalogue", "/catalogue"));
        }

        var today = DateTime.UtcNow.Date;
        var described = loans.Select(l =>
        {
            var days = (int)(l.DueDate.Date - today).TotalDays;
            return days < 0
                ? $"{l.Title} — {Math.Abs(days)} day(s) OVERDUE"
                : days == 0 ? $"{l.Title} — due today"
                : $"{l.Title} — due in {days} day(s)";
        });

        return AssistantAnswer.Say("account.loans",
            $"You have {loans.Count} book(s):\n" + string.Join("\n", described),
            new AssistantLink("My Library", "/portal"));
    }

    // ------------------------------------------------------------------ hours

    /// <summary>
    /// Opening hours, plus whether the doors are open right now — the thing a student actually
    /// wants when they ask, and something a printed table on a page cannot tell them.
    /// </summary>
    private AssistantAnswer AnswerHours()
    {
        // The library timezone, not the server one - the host runs on UTC.
        var openNow = _hours.IsOpenAt(_hours.LocalNow());

        var status = openNow
            ? "We are open right now."
            : "We are closed right now.";

        return AssistantAnswer.Say("hours", $"{status} {_hours.Sentence}");
    }

    // ------------------------------------------------------------------ policy

    private async Task<AssistantAnswer> AnswerPolicyAsync(string q, CancellationToken ct)
    {
        var policy = await _policies.GetLoanPolicyAsync(ct);

        if (q.Contains("how many"))
        {
            return AssistantAnswer.Say("policy.limit",
                $"You can borrow up to {policy.MaximumBooksPerStudent} books at once.");
        }

        if (q.Contains("late") || q.Contains("fine") || q.Contains("overdue"))
        {
            return AssistantAnswer.Say("policy.fine",
                $"Returning late costs {policy.Currency} {policy.FinePerDay:0.00} per day, per book."
                + (policy.GracePeriodDays > 0
                    ? $" There is a {policy.GracePeriodDays}-day grace period first."
                    : string.Empty));
        }

        return AssistantAnswer.Say("policy.duration",
            $"You can keep a book for up to {policy.MaximumLoanDays} days, "
            + $"and borrow up to {policy.MaximumBooksPerStudent} at a time.");
    }

    // ------------------------------------------------------------------ catalogue

    private async Task<AssistantAnswer> AnswerCatalogueAsync(string original, string lower, CancellationToken ct)
    {
        var title = ExtractTitle(original);

        if (string.IsNullOrWhiteSpace(title))
        {
            return AssistantAnswer.Say("catalogue.unclear",
                "Tell me the title you are looking for and I will check whether we have it and where it is.");
        }

        var matches = await _db.Books.AsNoTracking()
            .Where(b => b.Title.Contains(title) || b.Author.Contains(title))
            .Select(b => new { b.Id, b.Title, b.Author })
            .Take(5)
            .ToListAsync(ct);

        // Never claim to hold something we do not (specification section 12).
        if (matches.Count == 0)
        {
            return AssistantAnswer.Say("catalogue.notfound",
                $"I could not find anything matching “{title}” in the catalogue. "
                + "Try the author's name, or check the spelling.",
                new AssistantLink("Search the catalogue", "/catalogue"));
        }

        if (matches.Count > 1)
        {
            return new AssistantAnswer(
                $"I found {matches.Count} possible matches for “{title}”. Which did you mean?",
                "catalogue.ambiguous",
                matches.Select(m => new AssistantLink($"{m.Title} — {m.Author}", $"/catalogue/{m.Id}")).ToList());
        }

        var book = matches[0];

        var copies = await _db.BookCopies.AsNoTracking()
            .Include(c => c.LibrarySection)
            .Include(c => c.Shelf)
            .Include(c => c.ShelfPosition)
            .Where(c => c.BookId == book.Id && c.CopyNumber != "LEGACY")
            .ToListAsync(ct);

        var available = copies.Where(c => c.Status == BookCopyStatus.Available).ToList();
        var link = new AssistantLink($"Open {book.Title}", $"/catalogue/{book.Id}");

        if (copies.Count == 0)
        {
            return AssistantAnswer.Say("catalogue.nocopies",
                $"“{book.Title}” is in the catalogue, but the library holds no copies of it.", link);
        }

        if (available.Count == 0)
        {
            var due = await _db.BorrowingRecords.AsNoTracking()
                .Where(r => r.ReturnDate == null && r.BookCopy!.BookId == book.Id)
                .OrderBy(r => r.DueDate)
                .Select(r => (DateTime?)r.DueDate)
                .FirstOrDefaultAsync(ct);

            return AssistantAnswer.Say("catalogue.unavailable",
                $"“{book.Title}” is in the catalogue but every copy is on loan."
                + (due is null ? "" : $" The next one is due back on {due:dd MMMM yyyy}.")
                + " You can reserve it and we will email you when it is returned.", link);
        }

        // Location is reported only as precisely as it is recorded (specification section 9).
        var locations = available
            .Select(Describe)
            .Where(l => l is not null)
            .Distinct()
            .ToList();

        var count = available.Count == 1 ? "1 copy is available" : $"{available.Count} copies are available";

        var where = locations.Count switch
        {
            0 => " I do not have a shelf location recorded for it — ask at the desk.",
            1 => $" You will find it at {locations[0]}.",
            _ => " Copies are shelved at: " + string.Join("; ", locations) + "."
        };

        return AssistantAnswer.Say("catalogue.available",
            $"Yes — “{book.Title}” by {book.Author}. {count}.{where}", link);
    }

    private static string? Describe(BookCopy copy)
    {
        var parts = new List<string>();
        if (copy.LibrarySection?.Name is { Length: > 0 } s) parts.Add(s);
        if (copy.Shelf?.Name is { Length: > 0 } sh) parts.Add($"shelf {sh}");
        if (copy.ShelfPosition is not null) parts.Add($"position {copy.ShelfPosition.Position}");

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>
    /// Pulls a probable title out of natural phrasing. Quoted text wins; otherwise common
    /// lead-ins are stripped. Deliberately simple and predictable.
    /// </summary>
    internal static string ExtractTitle(string question)
    {
        var quoted = Regex.Match(question, "[\"“]([^\"”]{2,})[\"”]");
        if (quoted.Success)
        {
            return quoted.Groups[1].Value.Trim();
        }

        var text = question.Trim().TrimEnd('?', '.', '!');

        var leadIns = new[]
        {
            @"^where\s+(can\s+i\s+find|is|are)\s+",
            @"^do\s+you\s+have\s+(a\s+copy\s+of\s+|any\s+)?",
            @"^is\s+there\s+(a\s+copy\s+of\s+)?",
            @"^can\s+i\s+borrow\s+",
            @"^(i\s+(am\s+)?)?look(ing)?\s+for\s+",
            @"^find\s+(me\s+)?",
            @"^search\s+(for\s+)?",
            @"^show\s+me\s+"
        };

        foreach (var pattern in leadIns)
        {
            text = Regex.Replace(text, pattern, string.Empty, RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(text, @"^(the\s+book\s+|book\s+)", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+in\s+the\s+library$", string.Empty, RegexOptions.IgnoreCase);

        return text.Trim();
    }
}
