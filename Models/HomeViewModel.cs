using Library_Management_system.Models;

namespace Library_Management_system.Models;

public class HomeViewModel
{
    public List<Category> Categories { get; set; } = new();
    public List<Book> TrendingBooks { get; set; } = new();
    public List<Book> NewArrivalBooks { get; set; } = new();
    public List<Category> CategoryGenres { get; set; } = new();
    public HashSet<int> FavoriteBookIds { get; set; } = new();

    /// <summary>
    /// Available physical copies per book id, counted from BookCopy rather than the legacy
    /// Book.Quantity scalar. "Can I actually borrow this?" is the first thing a student wants
    /// from a book card, and the inherited homepage never answered it.
    /// Missing key means no copies are held.
    /// </summary>
    public Dictionary<int, int> AvailableCopies { get; set; } = new();

    // ---- catalogue at a glance -------------------------------------------
    // Shown in the hero, which used to carry a second search box duplicating the one
    // in the bar. Real figures, read at request time, rather than decoration.

    /// <summary>Distinct titles held.</summary>
    public int TitleCount { get; set; }

    /// <summary>Physical copies sitting on the shelf right now.</summary>
    public int AvailableNowCount { get; set; }

    /// <summary>Subjects with at least one book.</summary>
    public int SubjectCount { get; set; }

    public int AvailableFor(int bookId) =>
        AvailableCopies.TryGetValue(bookId, out var n) ? n : 0;
}
