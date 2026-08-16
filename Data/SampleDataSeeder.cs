using Library_Management_system.Models;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

// Fills a fresh database with a small browsable catalog.
// Skipped entirely once any book exists, so it never competes with real data.
public static class SampleDataSeeder
{
    private const string ImageRoot = "/images/User/Book";

    private sealed record SampleBook(
        string Category,
        string Title,
        string Author,
        string Isbn,
        int Year,
        int Pages,
        int Quantity,
        string Status,
        int Rating,
        string Image,
        string Description);

    private static readonly SampleBook[] Catalog =
    [
        new("Programming", "Clean Code", "Robert C. Martin", "9780132350884", 2008, 464, 5, "available", 5,
            "programming.jpg", "A handbook of agile software craftsmanship, covering naming, functions, formatting and the habits behind maintainable code."),
        new("Programming", "The Pragmatic Programmer", "Andrew Hunt", "9780201616224", 1999, 352, 3, "available", 5,
            "pg.jpg", "Practical advice on the craft of software development, from tracer bullets and rubber ducking to avoiding software rot."),
        new("Programming", "Design Patterns", "Erich Gamma", "9780201633610", 1994, 395, 2, "available", 4,
            "pg2.jpg", "The catalogue of twenty-three reusable object-oriented patterns that gave the industry a shared vocabulary for design."),

        new("Mathematics", "Calculus", "Michael Spivak", "9780914098911", 1967, 680, 4, "available", 5,
            "math.jpg", "A rigorous introduction to single-variable calculus that treats the subject as an first course in real analysis."),
        new("Mathematics", "Linear Algebra Done Right", "Sheldon Axler", "9783319110790", 1997, 340, 0, "unavailable", 4,
            "math1.jpg", "Builds linear algebra from vector spaces and linear maps, deferring determinants until the very end."),

        new("Science Fiction", "Dune", "Frank Herbert", "9780441013593", 1965, 412, 6, "available", 5,
            "science-fiction.jpg", "On the desert world of Arrakis, control of the spice melange decides the fate of empires, religions and bloodlines."),
        new("Science Fiction", "Neuromancer", "William Gibson", "9780441569595", 1984, 271, 3, "available", 4,
            "general.jpg", "A burned-out console cowboy takes one last job in cyberspace, in the novel that defined the cyberpunk genre."),
        new("Science Fiction", "Foundation", "Isaac Asimov", "9780553293357", 1951, 255, 2, "maintenance", 4,
            "general2.jpg", "Psychohistory predicts the fall of a galactic empire, and one plan aims to shorten the dark age that follows."),

        new("Law", "The Concept of Law", "H. L. A. Hart", "9780199644704", 1961, 315, 3, "available", 4,
            "law.jpg", "The foundational text of modern legal philosophy, separating law from coercion and morality through the rule of recognition."),

        new("Finance and Accounting", "The Intelligent Investor", "Benjamin Graham", "9780060555665", 1949, 623, 5, "available", 5,
            "finance.jpg", "The classic case for value investing, margin of safety and treating Mr. Market as a servant rather than a guide."),
        new("Finance and Accounting", "Accounting Principles", "Jerry J. Weygandt", "9781119707110", 2018, 1272, 4, "available", 4,
            "acc.jpg", "A comprehensive introduction to the accounting cycle, financial statements and managerial decision making."),

        new("Mystery", "The Hound of the Baskervilles", "Arthur Conan Doyle", "9780141032435", 1902, 256, 4, "available", 5,
            "mystery.jpg", "Sherlock Holmes investigates a spectral hound said to stalk the Baskerville line across the Devon moors."),
        new("Mystery", "Gone Girl", "Gillian Flynn", "9780307588371", 2012, 415, 2, "borrowed", 4,
            "novel.jpg", "A wife vanishes on her fifth wedding anniversary, and the husband's story stops adding up almost immediately."),

        new("Romance", "Pride and Prejudice", "Jane Austen", "9780141439518", 1813, 279, 5, "available", 5,
            "romance.jpg", "Elizabeth Bennet and Mr Darcy misjudge each other thoroughly before either is willing to reconsider.")
    ];

    public static async Task SeedAsync(ApplicationDbContext context, string actor)
    {
        if (await context.Books.AnyAsync())
        {
            return;
        }

        var now = DateTime.UtcNow;

        var categories = await ResolveCategoriesAsync(context, actor, now);
        var authors = await ResolveAuthorsAsync(context, actor, now);

        var sequence = 1;
        foreach (var sample in Catalog)
        {
            var category = categories[sample.Category];
            var author = authors[sample.Author];
            var imageUrl = $"{ImageRoot}/{sample.Image}";

            context.Books.Add(new Book
            {
                BookCode = $"BK-{sequence++:D3}",
                Title = sample.Title,
                Author = author.AuthorName,
                AuthorId = author.AuthorID,
                CategoryName = category.Name,
                CategoryId = category.Id,
                Isbn = sample.Isbn,
                Quantity = sample.Quantity,
                Availability = sample.Quantity > 0
                    && !string.Equals(sample.Status, "unavailable", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(sample.Status, "maintenance", StringComparison.OrdinalIgnoreCase),
                Pages = sample.Pages,
                Year = sample.Year,
                Status = sample.Status,
                Description = sample.Description,
                Summarized = sample.Description,
                ImageUrl = imageUrl,
                BookImage = imageUrl,
                Rating = sample.Rating,
                CreatedBy = actor,
                CreatedDate = now
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, Category>> ResolveCategoriesAsync(
        ApplicationDbContext context,
        string actor,
        DateTime now)
    {
        var existing = await context.Categories.ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Catalog.GroupBy(b => b.Category, StringComparer.OrdinalIgnoreCase))
        {
            if (existing.TryGetValue(group.Key, out var match))
            {
                resolved[group.Key] = match;
                continue;
            }

            var created = new Category
            {
                Name = group.Key,
                // Reuse the first title's cover so category cards are not blank.
                ImageUrl = $"{ImageRoot}/{group.First().Image}",
                Description = $"{group.Count()} title(s) in the {group.Key} collection.",
                CreatedBy = actor,
                CreatedDate = now
            };

            context.Categories.Add(created);
            resolved[group.Key] = created;
        }

        await context.SaveChangesAsync();
        return resolved;
    }

    private static async Task<Dictionary<string, Author>> ResolveAuthorsAsync(
        ApplicationDbContext context,
        string actor,
        DateTime now)
    {
        var existing = await context.Authors.ToDictionaryAsync(a => a.AuthorName, StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in Catalog.Select(b => b.Author).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existing.TryGetValue(name, out var match))
            {
                resolved[name] = match;
                continue;
            }

            var created = new Author
            {
                AuthorName = name,
                CreatedBy = actor,
                CreatedDate = now
            };

            context.Authors.Add(created);
            resolved[name] = created;
        }

        await context.SaveChangesAsync();
        return resolved;
    }
}
