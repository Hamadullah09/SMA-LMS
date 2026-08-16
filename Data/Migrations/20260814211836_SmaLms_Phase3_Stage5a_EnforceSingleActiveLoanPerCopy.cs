using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Library_Management_system.Data.Migrations
{
    /// <summary>
    /// Phase 3, stage 5a - the constraint that actually prevents double-issue
    /// (specification section 42).
    ///
    /// A unique filtered index permitting at most one OPEN loan per physical copy. This holds even
    /// if application logic is wrong, if two librarians click at the same instant, or if a duplicate
    /// RFID scan slips past debouncing. Concurrency tokens and service-layer checks are defence in
    /// depth on top of it - this is the guarantee.
    ///
    /// Deliberately split from stage 5b (making BookCopyId NOT NULL and dropping the legacy BookId
    /// column). Those are breaking changes for the inherited borrowing controller, which still
    /// writes title-level loans, and must wait until Phase 4 has moved every write path onto
    /// ICirculationService. Applying them now would leave the application unable to issue a book.
    /// </summary>
    public partial class SmaLms_Phase3_Stage5a_EnforceSingleActiveLoanPerCopy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Refuse to create the constraint if the data would already violate it - a clear
            // error beats an opaque index-creation failure.
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM BorrowingRecords
    WHERE ReturnDate IS NULL AND BookCopyId IS NOT NULL
    GROUP BY BookCopyId
    HAVING COUNT(*) > 1
)
BEGIN
    THROW 51001,
        N'Cannot enforce one-open-loan-per-copy: some physical copies already have more than one open loan. Resolve the duplicates, then re-run.',
        1;
END
");

            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX UX_BorrowingRecords_OneOpenLoanPerCopy
    ON BorrowingRecords (BookCopyId)
    WHERE ReturnDate IS NULL AND BookCopyId IS NOT NULL;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS UX_BorrowingRecords_OneOpenLoanPerCopy ON BorrowingRecords;");
        }
    }
}
