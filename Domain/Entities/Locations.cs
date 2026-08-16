using System.ComponentModel.DataAnnotations;

namespace Library_Management_system.Domain.Entities;

// Physical location hierarchy (specification section 9):
// Library -> Building -> Floor -> Section -> Room -> Rack -> Shelf -> Position
//
// Every level below Library is optional for a given copy: section 9 requires showing the most
// precise location available, so a copy known only to section level is representable.

public class Library
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Address { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Building> Buildings { get; set; } = new List<Building>();
}

public class Building
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Short label used in shelf references, e.g. "B1".</summary>
    [MaxLength(20)]
    public string? Code { get; set; }

    public int LibraryId { get; set; }
    public Library? Library { get; set; }

    public ICollection<Floor> Floors { get; set; } = new List<Floor>();
}

public class Floor
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Ordinal for sorting; ground floor is 0, basements negative.</summary>
    public int Level { get; set; }

    public int BuildingId { get; set; }
    public Building? Building { get; set; }

    public ICollection<LibrarySection> Sections { get; set; } = new List<LibrarySection>();
}

/// <summary>A subject area of the library, e.g. "Computer Science".</summary>
public class LibrarySection
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Code { get; set; }

    public int FloorId { get; set; }
    public Floor? Floor { get; set; }

    public ICollection<Room> Rooms { get; set; } = new List<Room>();
    public ICollection<Rack> Racks { get; set; } = new List<Rack>();
}

public class Room
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int LibrarySectionId { get; set; }
    public LibrarySection? LibrarySection { get; set; }

    public ICollection<Rack> Racks { get; set; } = new List<Rack>();
}

public class Rack
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    /// <summary>A rack belongs to a section, and optionally sits inside a room.</summary>
    public int LibrarySectionId { get; set; }
    public LibrarySection? LibrarySection { get; set; }

    public int? RoomId { get; set; }
    public Room? Room { get; set; }

    public ICollection<Shelf> Shelves { get; set; } = new List<Shelf>();
}

public class Shelf
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public int RackId { get; set; }
    public Rack? Rack { get; set; }

    public ICollection<ShelfPosition> Positions { get; set; } = new List<ShelfPosition>();
}

/// <summary>
/// The most precise location a copy can have - an ordinal slot on a shelf.
/// Optional: many libraries shelve to shelf level only.
/// </summary>
public class ShelfPosition
{
    public int Id { get; set; }

    public int ShelfId { get; set; }
    public Shelf? Shelf { get; set; }

    /// <summary>Ordinal position along the shelf, 1-based.</summary>
    public int Position { get; set; }

    [MaxLength(50)]
    public string? Label { get; set; }
}
