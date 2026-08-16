using System.ComponentModel;
using Library_Management_system.Data.Configuration;
using Library_Management_system.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }
        public DbSet<Book> Books { get; set; }
        public DbSet<LibraryEvent> Events { get; set; }
        public DbSet<CartItem> CartItems { get; set; }
        public DbSet<FavoriteBook> FavoriteBooks { get; set; }
        public DbSet<BorrowingRecord> BorrowingRecords { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<ContactMessage> ContactMessages { get; set; }
        public DbSet<BookReview> BookReviews { get; set; }
        public DbSet<Author> Authors { get; set; }
        public DbSet<Fine> Fines { get; set; }

        // ---- SMA LMS (Phase 3). Additive: nothing above is altered. ----

        // Catalog
        public DbSet<Domain.Entities.BookCopy> BookCopies { get; set; }

        // People
        public DbSet<Domain.Entities.Student> Students { get; set; }
        public DbSet<Domain.Entities.Department> Departments { get; set; }
        public DbSet<Domain.Entities.AcademicProgram> AcademicPrograms { get; set; }

        // Locations
        public DbSet<Domain.Entities.Library> Libraries { get; set; }
        public DbSet<Domain.Entities.Building> Buildings { get; set; }
        public DbSet<Domain.Entities.Floor> Floors { get; set; }
        public DbSet<Domain.Entities.LibrarySection> LibrarySections { get; set; }
        public DbSet<Domain.Entities.Room> Rooms { get; set; }
        public DbSet<Domain.Entities.Rack> Racks { get; set; }
        public DbSet<Domain.Entities.Shelf> Shelves { get; set; }
        public DbSet<Domain.Entities.ShelfPosition> ShelfPositions { get; set; }

        // RFID
        public DbSet<Domain.Entities.StudentRfidTag> StudentRfidTags { get; set; }
        public DbSet<Domain.Entities.BookRfidTag> BookRfidTags { get; set; }
        public DbSet<Domain.Entities.RfidReader> RfidReaders { get; set; }
        public DbSet<Domain.Entities.RfidScanEvent> RfidScanEvents { get; set; }
        public DbSet<Domain.Entities.RfidTransaction> RfidTransactions { get; set; }

        // Circulation
        public DbSet<Domain.Entities.Reservation> Reservations { get; set; }

        // Governance
        public DbSet<Domain.Entities.LibraryPolicy> LibraryPolicies { get; set; }
        public DbSet<Domain.Entities.AuditLog> AuditLogs { get; set; }
        public DbSet<Domain.Entities.SecurityEvent> SecurityEvents { get; set; }
        public DbSet<Domain.Entities.Notification> Notifications { get; set; }
        public DbSet<Domain.Entities.NotificationTemplate> NotificationTemplates { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.ConfigureSmaLms();

            builder.Entity<FavoriteBook>(entity =>
            {
                entity.HasIndex(x => new { x.OwnerKey, x.BookId }).IsUnique();
                entity.Property(x => x.OwnerKey).HasMaxLength(200);
            });

            builder.Entity<BookReview>(entity =>
            {
                entity.HasIndex(x => new { x.BookId, x.UserId }).IsUnique();
                entity.Property(x => x.UserId).HasMaxLength(450);
                entity.Property(x => x.Username).HasMaxLength(150);
                entity.Property(x => x.Email).HasMaxLength(256);

                entity.HasOne(x => x.Book)
                    .WithMany(x => x.Reviews)
                    .HasForeignKey(x => x.BookId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Author>(entity =>
            {
                entity.HasKey(x => x.AuthorID);
                entity.Property(x => x.AuthorID).HasColumnName("Id");
                entity.Property(x => x.AuthorName).HasColumnName("Name").HasMaxLength(100);
                entity.Property(x => x.CreatedBy).HasMaxLength(150);
                entity.Property(x => x.CreatedDate);
                entity.HasIndex(x => x.AuthorName).IsUnique();
            });

            builder.Entity<Book>(entity =>
            {
                entity.Property(x => x.Title).HasMaxLength(200);
                entity.Property(x => x.BookCode).HasMaxLength(50);
                entity.Property(x => x.BookImage).HasMaxLength(255);
                entity.Property(x => x.Summarized).HasMaxLength(500);

                entity.HasOne(x => x.Category)
                    .WithMany()
                    .HasForeignKey(x => x.CategoryId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.AuthorEntity)
                    .WithMany()
                    .HasForeignKey(x => x.AuthorId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<BorrowingRecord>(entity =>
            {
                entity.ToTable("BorrowingRecords");
                entity.Property(x => x.DurationDays).HasDefaultValue(14);
                entity.Property(x => x.ReturnUserId).HasMaxLength(450);

                entity.HasOne(x => x.Reservation)
                    .WithMany()
                    .HasForeignKey(x => x.ReservationId)
                    .OnDelete(DeleteBehavior.NoAction);

                // SMA LMS Phase 3 stage 4. Restrict everywhere: a borrowing record must never be
                // deleted as a side effect of removing a copy or a student.
                entity.HasOne(x => x.BookCopy)
                    .WithMany()
                    .HasForeignKey(x => x.BookCopyId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.Student)
                    .WithMany()
                    .HasForeignKey(x => x.StudentId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(x => x.TransactionNumber).IsUnique()
                    .HasFilter("[TransactionNumber] IS NOT NULL");
                entity.HasIndex(x => x.DueDate);
                entity.HasIndex(x => x.Status);
                entity.HasIndex(x => x.StudentId);
            });

            builder.Entity<Fine>(entity =>
            {
                entity.ToTable("fines");
                entity.HasKey(x => x.FineID);
                entity.Property(x => x.FineID).ValueGeneratedOnAdd();
                entity.Property(x => x.Amount).HasColumnType("decimal(10,2)");
                entity.Property(x => x.Paid).HasDefaultValue(false);
                entity.Property(x => x.Remark).HasMaxLength(1000);

                entity.HasIndex(x => x.BorrowID).IsUnique();

                entity.HasOne(x => x.Borrowing)
                    .WithOne(x => x.Fine)
                    .HasForeignKey<Fine>(x => x.BorrowID)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
