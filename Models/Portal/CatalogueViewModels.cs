namespace Library_Management_system.Models.Portal;

/// <summary>One card in the catalogue grid (specification section 94).</summary>
public sealed class CatalogueCard
{
    public int BookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? CoverImage { get; set; }
    public int? Year { get; set; }

    /// <summary>
    /// Derived from BookCopy rows, not the legacy Book.Quantity scalar. The copy table is now the
    /// source of truth for what physically exists.
    /// </summary>
    public int TotalCopies { get; set; }
    public int AvailableCopies { get; set; }

    /// <summary>Most precise shared location; null when copies are shelved differently or unknown.</summary>
    public string? LocationSummary { get; set; }

    public bool IsAvailable => AvailableCopies > 0;

    public string AvailabilityText => TotalCopies == 0
        ? "No copies held"
        : AvailableCopies == 0
            ? "All copies on loan"
            : AvailableCopies == 1
                ? "1 copy available"
                : $"{AvailableCopies} copies available";
}

public sealed class CatalogueViewModel
{
    public string? Query { get; set; }
    public string? Category { get; set; }
    public bool AvailableOnly { get; set; }
    public string Sort { get; set; } = "title";

    public IReadOnlyList<CatalogueCard> Results { get; set; } = [];
    public IReadOnlyList<string> Categories { get; set; } = [];

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 12;
    public int TotalCount { get; set; }
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Query) || !string.IsNullOrWhiteSpace(Category) || AvailableOnly;
}

/// <summary>Book detail (specification section 95).</summary>
public sealed class BookDetailViewModel
{
    public int BookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? Isbn { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CoverImage { get; set; }
    public int? Year { get; set; }
    public int? Pages { get; set; }

    public IReadOnlyList<CopyLine> Copies { get; set; } = [];

    public int AvailableCopies => Copies.Count(c => c.IsAvailable);
    public bool IsAvailable => AvailableCopies > 0;

    public int MaximumLoanDays { get; set; }

    public sealed class CopyLine
    {
        public string CopyNumber { get; set; } = string.Empty;
        public string? AccessionNumber { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsAvailable { get; set; }
        public string Location { get; set; } = "Location not recorded";
        public DateTime? DueBack { get; set; }
    }
}
