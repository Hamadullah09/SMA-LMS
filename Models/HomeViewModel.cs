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

    public int AvailableFor(int bookId) =>
        AvailableCopies.TryGetValue(bookId, out var n) ? n : 0;
}
