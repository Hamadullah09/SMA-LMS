using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Library_Management_system.Data.Migrations
{
    /// <inheritdoc />
    public partial class SmaLms_Phase3_Stage4_MapLoansToCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BookCopyId",
                table: "BorrowingRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IssueMethod",
                table: "BorrowingRecords",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IssueReaderId",
                table: "BorrowingRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReturnMethod",
                table: "BorrowingRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReturnReaderId",
                table: "BorrowingRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "BorrowingRecords",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StudentId",
                table: "BorrowingRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransactionNumber",
                table: "BorrowingRecords",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BorrowingRecords_BookCopyId",
                table: "BorrowingRecords",
                column: "BookCopyId");

            migrationBuilder.CreateIndex(
                name: "IX_BorrowingRecords_DueDate",
                table: "BorrowingRecords",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_BorrowingRecords_Status",
                table: "BorrowingRecords",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BorrowingRecords_StudentId",
                table: "BorrowingRecords",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_BorrowingRecords_TransactionNumber",
                table: "BorrowingRecords",
                column: "TransactionNumber",
                unique: true,
                filter: "[TransactionNumber] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_BorrowingRecords_BookCopies_BookCopyId",
                table: "BorrowingRecords",
                column: "BookCopyId",
                principalTable: "BookCopies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BorrowingRecords_Students_StudentId",
                table: "BorrowingRecords",
                column: "StudentId",
                principalTable: "Students",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            BackfillLoans(migrationBuilder);
        }

        /// <summary>
        /// Maps every existing borrowing record onto a physical copy.
        ///
        /// The inherited schema never recorded WHICH copy was borrowed - that information does not
        /// exist and cannot be recovered. So:
        ///
        ///   * OPEN loans claim a distinct real copy of the title, and that copy is marked Issued.
        ///     These matter operationally: a librarian needs to know what is actually out.
        ///   * CLOSED historical loans attach to a per-title copy explicitly numbered LEGACY and
        ///     marked Archived, so the history is preserved (specification section 87) but is never
        ///     presented as more precise than it really is.
        ///
        /// A pre-flight check runs first. If any title has more open loans than it has copies, the
        /// inherited data is already inconsistent, and the migration ABORTS rather than guessing.
        /// </summary>
        private static void BackfillLoans(MigrationBuilder migrationBuilder)
        {
            // ---------- Pre-flight: refuse to guess at inconsistent data ----------
            migrationBuilder.Sql(@"
DECLARE @Offenders nvarchar(max);

SELECT @Offenders = STRING_AGG(CAST(x.Title AS nvarchar(max)), ', ')
FROM (
    SELECT b.Title
    FROM BorrowingRecords br
    INNER JOIN Books b ON b.Id = br.BookId
    WHERE br.ReturnDate IS NULL
    GROUP BY b.Id, b.Title
    HAVING COUNT(*) > (SELECT COUNT(*) FROM BookCopies c WHERE c.BookId = b.Id)
) x;

IF @Offenders IS NOT NULL
BEGIN
    DECLARE @Message nvarchar(2048) =
        N'Phase 3 stage 4 aborted: these titles have more open loans than physical copies, '
        + N'so loans cannot be mapped to copies without inventing data. '
        + N'Correct the stock counts or close the stale loans, then re-run. Titles: '
        + @Offenders;
    THROW 51000, @Message, 1;
END
");

            // ---------- Open loans claim a distinct real copy ----------
            migrationBuilder.Sql(@"
WITH OpenLoans AS (
    SELECT br.Id AS LoanId, br.BookId,
           ROW_NUMBER() OVER (PARTITION BY br.BookId ORDER BY br.BorrowDate, br.Id) AS LoanSeq
    FROM BorrowingRecords br
    WHERE br.ReturnDate IS NULL AND br.BookCopyId IS NULL
),
FreeCopies AS (
    SELECT c.Id AS CopyId, c.BookId,
           ROW_NUMBER() OVER (PARTITION BY c.BookId ORDER BY c.CopyNumber) AS CopySeq
    FROM BookCopies c
    WHERE c.CopyNumber <> 'LEGACY'
)
UPDATE br
SET br.BookCopyId = fc.CopyId
FROM BorrowingRecords br
INNER JOIN OpenLoans ol ON ol.LoanId = br.Id
INNER JOIN FreeCopies fc ON fc.BookId = ol.BookId AND fc.CopySeq = ol.LoanSeq;
");

            // Those copies are physically out on loan.
            migrationBuilder.Sql(@"
UPDATE c
SET c.Status = 1 -- BookCopyStatus.Issued
FROM BookCopies c
INNER JOIN BorrowingRecords br ON br.BookCopyId = c.Id
WHERE br.ReturnDate IS NULL;
");

            // ---------- Closed loans attach to a per-title LEGACY copy ----------
            migrationBuilder.Sql(@"
INSERT INTO BookCopies (BookId, CopyNumber, Status, Condition, CreatedUtc, CreatedBy, StatusNote)
SELECT DISTINCT
    br.BookId,
    'LEGACY',
    7, -- BookCopyStatus.Archived
    1, -- BookCondition.Good
    GETUTCDATE(),
    'Phase3 Backfill',
    'Placeholder for borrowing history recorded before per-copy tracking existed. Not a physical item.'
FROM BorrowingRecords br
WHERE br.ReturnDate IS NOT NULL
  AND br.BookCopyId IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM BookCopies c WHERE c.BookId = br.BookId AND c.CopyNumber = 'LEGACY'
  );
");

            migrationBuilder.Sql(@"
UPDATE br
SET br.BookCopyId = c.Id
FROM BorrowingRecords br
INNER JOIN BookCopies c ON c.BookId = br.BookId AND c.CopyNumber = 'LEGACY'
WHERE br.BookCopyId IS NULL;
");

            // ---------- Link loans to students via the account that borrowed ----------
            migrationBuilder.Sql(@"
UPDATE br
SET br.StudentId = s.Id
FROM BorrowingRecords br
INNER JOIN AspNetUsers u
        ON u.UserName = br.Username OR u.Email = br.Username
INNER JOIN Students s ON s.ApplicationUserId = u.Id
WHERE br.StudentId IS NULL;
");

            // ---------- Historical loans were not RFID ----------
            // EF's AddColumn defaulted IssueMethod to 0 (Rfid) for existing rows, which would be
            // a false audit record. Every inherited loan predates RFID.
            migrationBuilder.Sql(@"
UPDATE BorrowingRecords
SET IssueMethod = 1, -- CirculationMethod.Manual
    ReturnMethod = CASE WHEN ReturnDate IS NOT NULL THEN 1 ELSE NULL END;
");

            // ---------- Assign transaction numbers ----------
            migrationBuilder.Sql(@"
WITH Numbered AS (
    SELECT Id, BorrowDate,
           ROW_NUMBER() OVER (ORDER BY BorrowDate, Id) AS Seq
    FROM BorrowingRecords
    WHERE TransactionNumber IS NULL
)
UPDATE br
SET br.TransactionNumber =
    'SMA-LIB-' + CAST(YEAR(n.BorrowDate) AS varchar(4)) + '-'
    + RIGHT('000000' + CAST(n.Seq AS varchar(10)), 6)
FROM BorrowingRecords br
INNER JOIN Numbered n ON n.Id = br.Id;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BorrowingRecords_BookCopies_BookCopyId",
                table: "BorrowingRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_BorrowingRecords_Students_StudentId",
                table: "BorrowingRecords");

            migrationBuilder.DropIndex(
                name: "IX_BorrowingRecords_BookCopyId",
                table: "BorrowingRecords");

            migrationBuilder.DropIndex(
                name: "IX_BorrowingRecords_DueDate",
                table: "BorrowingRecords");

            migrationBuilder.DropIndex(
                name: "IX_BorrowingRecords_Status",
                table: "BorrowingRecords");

            migrationBuilder.DropIndex(
                name: "IX_BorrowingRecords_StudentId",
                table: "BorrowingRecords");

            migrationBuilder.DropIndex(
                name: "IX_BorrowingRecords_TransactionNumber",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "BookCopyId",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "IssueMethod",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "IssueReaderId",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "ReturnMethod",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "ReturnReaderId",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "StudentId",
                table: "BorrowingRecords");

            migrationBuilder.DropColumn(
                name: "TransactionNumber",
                table: "BorrowingRecords");
        }
    }
}
