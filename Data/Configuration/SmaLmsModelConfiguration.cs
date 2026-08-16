using Library_Management_system.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data.Configuration;

/// <summary>
/// Model configuration for the SMA LMS entities added in Phase 3.
///
/// Kept separate from ApplicationDbContext.OnModelCreating so the inherited configuration stays
/// readable and the new model can be reviewed on its own.
///
/// Delete behaviour is Restrict almost everywhere: SQL Server rejects multiple cascade paths, and
/// specification section 87 forbids deleting borrowing history or RFID assignments anyway.
/// </summary>
public static class SmaLmsModelConfiguration
{
    public static void ConfigureSmaLms(this ModelBuilder builder)
    {
        ConfigureLocations(builder);
        ConfigurePeople(builder);
        ConfigureCatalog(builder);
        ConfigureRfid(builder);
        ConfigureGovernance(builder);
    }

    private static void ConfigureLocations(ModelBuilder builder)
    {
        builder.Entity<Library>(e =>
        {
            e.ToTable("Libraries");
            e.HasIndex(x => x.Name);
        });

        builder.Entity<Building>(e =>
        {
            e.HasOne(x => x.Library).WithMany(x => x.Buildings)
                .HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Floor>(e =>
        {
            e.HasOne(x => x.Building).WithMany(x => x.Floors)
                .HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LibrarySection>(e =>
        {
            e.ToTable("LibrarySections");
            e.HasOne(x => x.Floor).WithMany(x => x.Sections)
                .HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Name);
        });

        builder.Entity<Room>(e =>
        {
            e.HasOne(x => x.LibrarySection).WithMany(x => x.Rooms)
                .HasForeignKey(x => x.LibrarySectionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Rack>(e =>
        {
            e.HasOne(x => x.LibrarySection).WithMany(x => x.Racks)
                .HasForeignKey(x => x.LibrarySectionId).OnDelete(DeleteBehavior.Restrict);

            // A rack optionally sits inside a room; NoAction avoids a second cascade path
            // into LibrarySection.
            e.HasOne(x => x.Room).WithMany(x => x.Racks)
                .HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Shelf>(e =>
        {
            e.HasOne(x => x.Rack).WithMany(x => x.Shelves)
                .HasForeignKey(x => x.RackId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ShelfPosition>(e =>
        {
            e.HasOne(x => x.Shelf).WithMany(x => x.Positions)
                .HasForeignKey(x => x.ShelfId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ShelfId, x.Position }).IsUnique();
        });
    }

    private static void ConfigurePeople(ModelBuilder builder)
    {
        builder.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        builder.Entity<AcademicProgram>(e =>
        {
            e.ToTable("AcademicPrograms");
            e.HasOne(x => x.Department).WithMany(x => x.Programs)
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Student>(e =>
        {
            // Specification section 50: both must be unique and indexed.
            e.HasIndex(x => x.RollNumber).IsUnique();
            e.HasIndex(x => x.StudentIdNumber).IsUnique();
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.ApplicationUserId);
            e.HasIndex(x => x.Status);

            e.HasOne(x => x.Department).WithMany()
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.AcademicProgram).WithMany()
                .HasForeignKey(x => x.AcademicProgramId).OnDelete(DeleteBehavior.Restrict);

            // Computed display helper, not a column.
            e.Ignore(x => x.MaskedCnic);
        });
    }

    private static void ConfigureCatalog(ModelBuilder builder)
    {
        builder.Entity<BookCopy>(e =>
        {
            e.ToTable("BookCopies");

            // One copy number per title.
            e.HasIndex(x => new { x.BookId, x.CopyNumber }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.AccessionNumber);
            e.HasIndex(x => x.ShelfPositionId);

            e.Property(x => x.AcquisitionCost).HasColumnType("decimal(10,2)");

            e.HasOne(x => x.Book).WithMany()
                .HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.ShelfPosition).WithMany()
                .HasForeignKey(x => x.ShelfPositionId).OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Shelf).WithMany()
                .HasForeignKey(x => x.ShelfId).OnDelete(DeleteBehavior.NoAction);

            e.HasOne(x => x.LibrarySection).WithMany()
                .HasForeignKey(x => x.LibrarySectionId).OnDelete(DeleteBehavior.NoAction);

            e.Ignore(x => x.IsBorrowable);
        });
    }

    private static void ConfigureRfid(ModelBuilder builder)
    {
        builder.Entity<StudentRfidTag>(e =>
        {
            e.ToTable("StudentRfidTags");

            // Exactly one LIVE assignment per EPC. Historical rows keep the same EPC with
            // IsActive = 0, so the filter is essential (specification sections 6, 36, 87).
            e.HasIndex(x => x.Epc).IsUnique().HasFilter("[IsActive] = 1");
            e.HasIndex(x => x.Epc);
            e.HasIndex(x => new { x.StudentId, x.IsActive });

            e.HasOne(x => x.Student).WithMany(x => x.RfidTags)
                .HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookRfidTag>(e =>
        {
            e.ToTable("BookRfidTags");

            e.HasIndex(x => x.Epc).IsUnique().HasFilter("[IsActive] = 1");
            e.HasIndex(x => x.Epc);
            e.HasIndex(x => new { x.BookCopyId, x.IsActive });

            e.HasOne(x => x.BookCopy).WithMany(x => x.RfidTags)
                .HasForeignKey(x => x.BookCopyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RfidReader>(e =>
        {
            e.ToTable("RfidReaders");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Status);

            e.HasOne(x => x.LibrarySection).WithMany()
                .HasForeignKey(x => x.LibrarySectionId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RfidScanEvent>(e =>
        {
            e.ToTable("RfidScanEvents");
            e.HasIndex(x => new { x.ReaderId, x.LastObservedUtc });
            e.HasIndex(x => x.Epc);
            e.HasIndex(x => x.CorrelationId);

            e.HasOne(x => x.Reader).WithMany(x => x.ScanEvents)
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RfidTransaction>(e =>
        {
            e.ToTable("RfidTransactions");
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.CreatedUtc);

            e.HasOne(x => x.ScanEvent).WithMany()
                .HasForeignKey(x => x.ScanEventId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureGovernance(ModelBuilder builder)
    {
        builder.Entity<LibraryPolicy>(e =>
        {
            e.ToTable("LibraryPolicies");
            e.HasIndex(x => x.Key).IsUnique();
        });

        builder.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasIndex(x => x.OccurredUtc);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.Operation);
        });

        builder.Entity<SecurityEvent>(e =>
        {
            e.ToTable("SecurityEvents");
            e.HasIndex(x => x.OccurredUtc);
            e.HasIndex(x => new { x.Kind, x.IsAcknowledged });

            e.HasOne(x => x.Reader).WithMany()
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Notification>(e =>
        {
            e.ToTable("Notifications");

            // Idempotency: one logical notification, however many catch-up passes run
            // (specification section 24).
            e.HasIndex(x => x.DeduplicationKey).IsUnique();

            // Outbox polling index (specification section 50).
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.HasIndex(x => x.StudentId);

            e.HasOne(x => x.Student).WithMany()
                .HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<NotificationTemplate>(e =>
        {
            e.ToTable("NotificationTemplates");
            e.HasIndex(x => new { x.Kind, x.Channel }).IsUnique();
        });

        builder.Entity<Reservation>(e =>
        {
            e.ToTable("Reservations");

            // Queue lookup: "who is next for this title" (specification section 26).
            e.HasIndex(x => new { x.BookId, x.Status, x.QueuePosition });
            e.HasIndex(x => new { x.StudentId, x.Status });

            // Expiry sweep by the background service.
            e.HasIndex(x => x.ExpiresUtc);

            // One open hold per student per title - a student cannot queue twice for one book.
            e.HasIndex(x => new { x.StudentId, x.BookId })
                .IsUnique()
                .HasFilter("[Status] IN (0, 1)");

            e.HasOne(x => x.Student).WithMany()
                .HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Book).WithMany()
                .HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.ReservedCopy).WithMany()
                .HasForeignKey(x => x.ReservedCopyId).OnDelete(DeleteBehavior.NoAction);

            e.Ignore(x => x.IsOpen);
        });
    }
}
