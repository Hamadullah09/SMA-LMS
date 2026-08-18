using System.Collections.Generic;

namespace Library_Management_system.Models
{
    public class BookDetailViewModel
    {
        public Book Book { get; set; } = new();
        public List<Book> RelatedBooks { get; set; } = new();
        public bool IsFavorite { get; set; }
        public HashSet<int> RelatedFavoriteBookIds { get; set; } = new();

        /// <summary>
        /// Borrowable copies of the book being viewed, counted from BookCopy.
        ///
        /// The detail page is where a student decides whether to walk to a shelf, and it was the one
        /// page that never answered "can I actually borrow this?" — it showed a star rating, a page
        /// count and a book code instead. Counted from copies, never Book.Quantity, so it agrees
        /// with what the kiosk would allow.
        /// </summary>
        public int AvailableCopies { get; set; }

        public int TotalCopies { get; set; }

        /// <summary>Favourites and availability for the related-book cards.</summary>
        public HomeViewModel RelatedCards { get; set; } = new();

        public bool IsBorrowable => AvailableCopies > 0;

        public string AvailabilityText => TotalCopies == 0
            ? "No copies held"
            : AvailableCopies == 0
                ? "All copies on loan"
                : AvailableCopies == 1
                    ? "1 copy available"
                    : $"{AvailableCopies} copies available";
    }
}
