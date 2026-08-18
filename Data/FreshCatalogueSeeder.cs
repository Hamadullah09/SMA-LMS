using System.Globalization;
using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Models;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>
/// Replaces the entire book side of the library with a fresh catalogue built from a
/// stock-code CSV, one physical copy per tag.
/// </summary>
/// <remarks>
/// DESTRUCTIVE, and deliberately not wired into normal startup. It runs only when
/// <c>Catalogue:FreshImport</c> is true in configuration, and it refuses to run twice against the
/// same file by recording nothing — the operator flips the flag off again afterwards.
///
/// Students, their logins and their RFID cards are left alone. "Start from fresh data" means fresh
/// stock; the accounts and enrolled cards are separate work and deleting them would throw away
/// something nobody asked to lose.
///
/// Open loans are closed rather than deleted, so a copy is never left marked Issued against a
/// borrowing row that no longer exists — that state is what makes the exit gate alarm on a book
/// that is legitimately on the shelf.
/// </remarks>
public static class FreshCatalogueSeeder
{
    /// <summary>Target copies per title. See <see cref="PlanBooks"/> for why sizes vary 7-9.</summary>
    private const int TargetCopiesPerBook = 8;

    /// <summary>Titles to create.</summary>
    private const int TargetBookCount = 50;

    public sealed record ImportOutcome(
        int BooksCreated,
        int CopiesCreated,
        int TagsCreated,
        int LoansClosed,
        int BooksDeleted,
        int CopiesDeleted,
        int TagsDeleted,
        IReadOnlyList<string> UnreadableEpcStockCodes);

    public static async Task<ImportOutcome> RunAsync(
        ApplicationDbContext db,
        string csvPath,
        CancellationToken ct = default)
    {
        var rows = ReadCsv(csvPath);

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No usable rows in '{csvPath}'.");
        }

        // An EPC a reader can never report is worth surfacing rather than burying: the copy will
        // exist, but it can never be checked out at the pad and will alarm at the exit gate.
        var unreadable = rows
            .Where(r => !IsHex(r.Epc))
            .Select(r => r.StockCode)
            .ToList();

        // The context is configured with a retrying execution strategy, which refuses a
        // hand-rolled transaction: a retry would resume mid-transaction with no way to redo the
        // earlier statements. Handing the whole unit to the strategy lets it replay the lot.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
            await ImportAsync(db, rows, unreadable, ct));
    }

    private static async Task<ImportOutcome> ImportAsync(
        ApplicationDbContext db,
        List<TagRow> rows,
        List<string> unreadable,
        CancellationToken ct)
    {
        // A replay starts from a clean slate: entities added by a failed attempt are still tracked
        // and would be inserted twice.
        db.ChangeTracker.Clear();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (loansClosed, booksDeleted, copiesDeleted, tagsDeleted) = await ClearBookSideAsync(db, ct);

        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        var plan = PlanBooks(rows);

        var now = DateTime.UtcNow;

        // Books carry both a denormalised author name and a real foreign key, so the author row has
        // to exist before the book does. Existing authors are reused by name rather than duplicated.
        var authorIds = await db.Authors
            .ToDictionaryAsync(a => a.AuthorName, a => a.AuthorID, StringComparer.OrdinalIgnoreCase, ct);

        var newAuthors = CatalogueTemplates
            .Select(t => t.Author)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !authorIds.ContainsKey(name))
            .Select(name => new Author
            {
                AuthorName = name,
                CreatedBy = "Fresh catalogue import",
                CreatedDate = now
            })
            .ToList();

        if (newAuthors.Count > 0)
        {
            db.Authors.AddRange(newAuthors);
            await db.SaveChangesAsync(ct);

            foreach (var author in newAuthors)
            {
                authorIds[author.AuthorName] = author.AuthorID;
            }
        }

        var books = new List<Book>();

        for (var i = 0; i < plan.Count; i++)
        {
            var group = plan[i];
            var template = CatalogueTemplates[i % CatalogueTemplates.Count];
            var category = categories.FirstOrDefault(c => c.Name == template.Category)
                           ?? categories.First();

            var book = new Book
            {
                // The code a student sees is a stock code from the sheet - the first one in this
                // book's run - so the label on the shelf and the code on screen agree.
                BookCode = group[0].StockCode,
                Title = template.Title,
                Author = template.Author,
                CategoryId = category.Id,
                CategoryName = category.Name,
                AuthorId = authorIds[template.Author],
                Isbn = BuildIsbn(i),
                Year = template.Year,
                Pages = template.Pages,
                Quantity = group.Count,
                Availability = true,
                Status = "available",
                Rating = template.Rating,
                Description = template.Description,
                // One generated cover per title (tools/generate-covers.js). Pointing every book at
                // the same placeholder is what made the catalogue look like one book fifty times.
                ImageUrl = $"/images/User/Book/covers/{Slug(template.Title)}.svg",
                CreatedBy = "Fresh catalogue import",
                CreatedDate = now
            };

            books.Add(book);
        }

        db.Books.AddRange(books);
        await db.SaveChangesAsync(ct);

        var copies = new List<BookCopy>();
        var tags = new List<BookRfidTag>();

        for (var i = 0; i < plan.Count; i++)
        {
            var group = plan[i];
            var book = books[i];

            for (var c = 0; c < group.Count; c++)
            {
                var row = group[c];

                var copy = new BookCopy
                {
                    BookId = book.Id,
                    // Sequential within the title; the accession number carries the real identity.
                    CopyNumber = (c + 1).ToString("000", CultureInfo.InvariantCulture),
                    AccessionNumber = row.StockCode,
                    Status = BookCopyStatus.Available,
                    Condition = BookCondition.Good,
                    AcquisitionDate = now,
                    AcquisitionSource = "Fresh catalogue import",
                    CreatedBy = "Fresh catalogue import",
                    CreatedUtc = now
                };

                copies.Add(copy);
            }
        }

        db.BookCopies.AddRange(copies);
        await db.SaveChangesAsync(ct);

        // Copies were added in plan order, so walking both in the same order pairs each copy with
        // the row it came from without a second lookup.
        var flatRows = plan.SelectMany(g => g).ToList();

        for (var i = 0; i < copies.Count; i++)
        {
            tags.Add(new BookRfidTag
            {
                BookCopyId = copies[i].Id,
                Epc = Normalise(flatRows[i].Epc),
                State = RfidTagState.Active,
                IsActive = true,
                AssignedUtc = now,
                AssignedBy = "Fresh catalogue import"
            });
        }

        db.BookRfidTags.AddRange(tags);
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        return new ImportOutcome(
            books.Count, copies.Count, tags.Count,
            loansClosed, booksDeleted, copiesDeleted, tagsDeleted,
            unreadable);
    }

    // ------------------------------------------------------------------ clearing

    private static async Task<(int Loans, int Books, int Copies, int Tags)> ClearBookSideAsync(
        ApplicationDbContext db, CancellationToken ct)
    {
        // Closed, not deleted: an open loan pointing at a copy that is about to disappear leaves
        // the student's history claiming they still hold a book that no longer exists.
        var openLoans = await db.BorrowingRecords.Where(r => r.ReturnDate == null).ToListAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var loan in openLoans)
        {
            loan.ReturnDate = now;
            loan.Status = "returned";
        }

        await db.SaveChangesAsync(ct);

        var tags = await db.BookRfidTags.CountAsync(ct);
        var copies = await db.BookCopies.CountAsync(ct);
        var books = await db.Books.CountAsync(ct);

        // Order matters: children before parents, or the FKs refuse.
        await db.BookRfidTags.ExecuteDeleteAsync(ct);
        await db.Fines.ExecuteDeleteAsync(ct);
        await db.BorrowingRecords.ExecuteDeleteAsync(ct);
        await db.Reservations.ExecuteDeleteAsync(ct);
        await db.CartItems.ExecuteDeleteAsync(ct);
        await db.FavoriteBooks.ExecuteDeleteAsync(ct);
        await db.BookReviews.ExecuteDeleteAsync(ct);
        await db.BookCopies.ExecuteDeleteAsync(ct);
        await db.Books.ExecuteDeleteAsync(ct);

        return (openLoans.Count, books, copies, tags);
    }

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Splits the rows into <see cref="TargetBookCount"/> runs of roughly
    /// <see cref="TargetCopiesPerBook"/>, never letting a run straddle two stock-code prefixes.
    /// </summary>
    /// <remarks>
    /// 400 rows over 50 books is exactly 8 each, but the prefixes come in hundreds and fifties and
    /// neither divides by 8. Books are apportioned to each prefix in proportion to its size and the
    /// remainder is spread one copy at a time, so sizes land at 7-9 and both totals still come out
    /// exact.
    /// </remarks>
    private static List<List<TagRow>> PlanBooks(List<TagRow> rows)
    {
        var groups = rows
            .GroupBy(r => r.Prefix)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

        var total = rows.Count;

        // Largest-remainder apportionment, so the book counts sum to exactly TargetBookCount.
        var quotas = groups
            .Select(g => (double)g.Count / total * TargetBookCount)
            .ToList();

        var counts = quotas.Select(q => (int)Math.Floor(q)).ToList();
        var shortfall = TargetBookCount - counts.Sum();

        foreach (var idx in quotas
                     .Select((q, i) => (Index: i, Remainder: q - Math.Floor(q)))
                     .OrderByDescending(x => x.Remainder)
                     .Take(shortfall)
                     .Select(x => x.Index))
        {
            counts[idx]++;
        }

        var plan = new List<List<TagRow>>();

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var books = Math.Max(1, counts[g]);

            var baseSize = group.Count / books;
            var extra = group.Count % books;   // the first `extra` books take one more

            var offset = 0;
            for (var b = 0; b < books; b++)
            {
                var size = baseSize + (b < extra ? 1 : 0);
                plan.Add(group.GetRange(offset, size));
                offset += size;
            }
        }

        return plan;
    }

    // ------------------------------------------------------------------ csv

    private sealed record TagRow(string Epc, string StockCode, string Prefix);

    private static List<TagRow> ReadCsv(string path)
    {
        var rows = new List<TagRow>();
        var seenEpc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path).Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            var epc = Normalise(parts[0]);
            var stock = parts[1].Trim();

            if (epc.Length == 0 || stock.Length == 0)
            {
                continue;
            }

            // A repeated EPC would breach the unique index and abort the whole import; a repeated
            // stock code would give two copies the same accession number.
            if (!seenEpc.Add(epc) || !seenStock.Add(stock))
            {
                continue;
            }

            var prefix = stock.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            rows.Add(new TagRow(epc, stock, prefix));
        }

        return rows;
    }

    /// <summary>Strips every space and upper-cases, matching RfidTagService.Normalise.</summary>
    private static string Normalise(string value) =>
        new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static bool IsHex(string value) =>
        value.Length > 0 && value.All(Uri.IsHexDigit);

    /// <summary>Matches the filenames produced by tools/generate-covers.js.</summary>
    private static string Slug(string value)
    {
        var lowered = value.ToLowerInvariant();
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var joined = new string(chars);

        while (joined.Contains("--"))
        {
            joined = joined.Replace("--", "-");
        }

        return joined.Trim('-');
    }

    private static string BuildIsbn(int index) =>
        $"978-0-{(100 + index):000}-{(10000 + index * 7):00000}-{index % 10}";

    // ------------------------------------------------------------------ titles

    private sealed record Template(
        string Title, string Author, string Category, int Year, int Pages, int Rating, string Description);

    /// <summary>
    /// Titles for the generated catalogue, spread across the categories the library already has.
    /// </summary>
    private static readonly List<Template> CatalogueTemplates =
    [
        new("Clean Code", "Robert C. Martin", "Programming", 2008, 464, 5, "A handbook of agile software craftsmanship."),
        new("The Pragmatic Programmer", "Andrew Hunt", "Programming", 1999, 352, 5, "From journeyman to master."),
        new("Design Patterns", "Erich Gamma", "Programming", 1994, 395, 5, "Elements of reusable object-oriented software."),
        new("Introduction to Algorithms", "Thomas H. Cormen", "Programming", 2009, 1312, 5, "The standard algorithms reference."),
        new("Code Complete", "Steve McConnell", "Programming", 2004, 960, 4, "A practical handbook of software construction."),
        new("Refactoring", "Martin Fowler", "Programming", 2018, 448, 5, "Improving the design of existing code."),
        new("The Mythical Man-Month", "Frederick P. Brooks", "Programming", 1975, 322, 4, "Essays on software engineering."),
        new("Structure and Interpretation of Computer Programs", "Harold Abelson", "Programming", 1985, 657, 5, "The classic MIT text."),
        new("Operating System Concepts", "Abraham Silberschatz", "Programming", 2018, 1040, 4, "Processes, memory and file systems."),
        new("Database System Concepts", "Henry F. Korth", "Programming", 2019, 1376, 4, "Relational design and transactions."),
        new("Computer Networks", "Andrew S. Tanenbaum", "Programming", 2010, 960, 4, "Protocols from the link layer up."),
        new("Artificial Intelligence: A Modern Approach", "Stuart Russell", "Programming", 2020, 1136, 5, "The standard AI text."),

        new("Calculus", "James Stewart", "Mathematics", 2015, 1368, 4, "Single and multivariable calculus."),
        new("Linear Algebra and Its Applications", "David C. Lay", "Mathematics", 2015, 576, 4, "Vector spaces and transformations."),
        new("Discrete Mathematics and Its Applications", "Kenneth H. Rosen", "Mathematics", 2018, 1120, 4, "Logic, sets, counting and graphs."),
        new("Principles of Mathematical Analysis", "Walter Rudin", "Mathematics", 1976, 342, 5, "Real analysis, rigorously."),
        new("Probability and Statistics", "Morris H. DeGroot", "Mathematics", 2011, 912, 4, "Distributions and inference."),
        new("Numerical Analysis", "Richard L. Burden", "Mathematics", 2015, 912, 4, "Approximation and error."),
        new("Abstract Algebra", "David S. Dummit", "Mathematics", 2003, 944, 4, "Groups, rings and fields."),
        new("Differential Equations", "Dennis G. Zill", "Mathematics", 2016, 480, 4, "Ordinary and partial equations."),

        new("The Concept of Law", "H. L. A. Hart", "Law", 1961, 315, 5, "A foundational text in legal philosophy."),
        new("Constitutional Law", "Erwin Chemerinsky", "Law", 2019, 1808, 4, "Principles and policies."),
        new("Contract Law", "Ewan McKendrick", "Law", 2020, 1200, 4, "Text, cases and materials."),
        new("Criminal Law", "Jonathan Herring", "Law", 2020, 928, 4, "Text, cases and materials."),
        new("International Law", "Malcolm N. Shaw", "Law", 2017, 1064, 4, "Public international law."),
        new("Company Law", "Alan Dignam", "Law", 2020, 560, 4, "Corporate governance and structure."),

        new("Principles of Accounting", "Jerry J. Weygandt", "Finance and Accounting", 2018, 1272, 4, "Financial and managerial accounting."),
        new("The Intelligent Investor", "Benjamin Graham", "Finance and Accounting", 1949, 640, 5, "The definitive book on value investing."),
        new("Corporate Finance", "Stephen A. Ross", "Finance and Accounting", 2018, 1024, 4, "Valuation and capital structure."),
        new("Financial Accounting", "Walter T. Harrison", "Finance and Accounting", 2017, 816, 4, "Statements and analysis."),
        new("Investments", "Zvi Bodie", "Finance and Accounting", 2020, 1024, 4, "Portfolio theory and practice."),
        new("Managerial Economics", "Paul Keat", "Finance and Accounting", 2013, 576, 4, "Economic tools for decisions."),
        new("Auditing and Assurance Services", "Alvin A. Arens", "Finance and Accounting", 2016, 880, 4, "An integrated approach."),
        new("Cost Accounting", "Charles T. Horngren", "Finance and Accounting", 2014, 976, 4, "A managerial emphasis."),

        new("Dune", "Frank Herbert", "Science Fiction", 1965, 688, 5, "The desert planet Arrakis and its spice."),
        new("Foundation", "Isaac Asimov", "Science Fiction", 1951, 255, 5, "Psychohistory and the fall of empire."),
        new("Neuromancer", "William Gibson", "Science Fiction", 1984, 271, 4, "The novel that named cyberspace."),
        new("The Left Hand of Darkness", "Ursula K. Le Guin", "Science Fiction", 1969, 304, 5, "An envoy on a world without fixed gender."),
        new("Snow Crash", "Neal Stephenson", "Science Fiction", 1992, 440, 4, "The Metaverse, before it was a slogan."),
        new("The Dispossessed", "Ursula K. Le Guin", "Science Fiction", 1974, 341, 5, "An ambiguous utopia."),

        new("The Hound of the Baskervilles", "Arthur Conan Doyle", "Mystery", 1902, 256, 5, "Holmes on the moor."),
        new("Gone Girl", "Gillian Flynn", "Mystery", 2012, 432, 4, "A disappearance and two unreliable accounts."),
        new("The Girl with the Dragon Tattoo", "Stieg Larsson", "Mystery", 2005, 672, 4, "A journalist and a hacker."),
        new("And Then There Were None", "Agatha Christie", "Mystery", 1939, 272, 5, "Ten strangers on an island."),
        new("The Big Sleep", "Raymond Chandler", "Mystery", 1939, 231, 4, "Marlowe's first case."),

        new("Pride and Prejudice", "Jane Austen", "Romance", 1813, 279, 5, "Elizabeth Bennet and Mr Darcy."),
        new("Jane Eyre", "Charlotte Bronte", "Romance", 1847, 507, 5, "A governess at Thornfield Hall."),
        new("Wuthering Heights", "Emily Bronte", "Romance", 1847, 416, 4, "Heathcliff and Catherine on the moors."),
        new("Persuasion", "Jane Austen", "Romance", 1817, 249, 4, "Anne Elliot, eight years on."),
        new("Sense and Sensibility", "Jane Austen", "Romance", 1811, 409, 4, "The Dashwood sisters.")
    ];
}
