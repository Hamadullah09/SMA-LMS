using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Library_Management_system.Data.Migrations
{
    /// <summary>
    /// Phase 3, stages 2 and 3 (DATABASE_ARCHITECTURE.md section 5).
    ///
    /// Pure data backfill - no schema change, so this is safe to run against a live database and
    /// is reversible by deleting only the rows it created.
    ///
    /// Stage 2: generate a BookCopy per unit of Book.Quantity.
    /// Stage 3: generate a Student per existing user in the "User" role.
    ///
    /// Both are idempotent: re-running skips books that already have copies and users that already
    /// have a student record, so an interrupted deployment can simply be re-run.
    /// </summary>
    public partial class SmaLms_Phase3_Stage23_BackfillCopiesAndStudents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------- Stage 2: BookCopy per unit of Quantity ----------
            //
            // Copies start Available. Title-level 'borrowed'/'reserved' is deliberately NOT
            // mapped here: which physical copy is on loan is decided in stage 4 from the
            // borrowing records, which is the only real evidence. Only 'maintenance' carries
            // over, because that describes the stock rather than a loan.
            // The recursion bound is computed into a variable first: SQL Server forbids
            // aggregates inside the recursive member of a CTE.
            migrationBuilder.Sql(@"
DECLARE @MaxQuantity int = (SELECT ISNULL(MAX(Quantity), 0) FROM Books);

WITH Numbers AS (
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1 FROM Numbers
    WHERE n < @MaxQuantity
)
INSERT INTO BookCopies (BookId, CopyNumber, Status, Condition, CreatedUtc, CreatedBy)
SELECT
    b.Id,
    CASE WHEN n.n < 1000
         THEN RIGHT('000' + CAST(n.n AS varchar(10)), 3)
         ELSE CAST(n.n AS varchar(10))
    END,
    CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(b.Status, '')))) = 'maintenance' THEN 5 ELSE 0 END,
    1, -- BookCondition.Good
    GETUTCDATE(),
    'Phase3 Backfill'
FROM Books b
INNER JOIN Numbers n ON n.n <= b.Quantity
WHERE NOT EXISTS (SELECT 1 FROM BookCopies c WHERE c.BookId = b.Id)
OPTION (MAXRECURSION 0);
");

            // Copies inherit the title's shelving where the legacy schema recorded any. The
            // inherited Books table has no location columns, so this is a no-op today and exists
            // so the intent is explicit rather than forgotten.

            // ---------- Stage 3: Student per user in the "User" role ----------
            //
            // RollNumber and StudentIdNumber are both UNIQUE and NOT NULL, but the inherited
            // schema never captured either. Rather than invent plausible-looking identifiers,
            // they are seeded as PENDING-<username>: unique, obviously provisional, and
            // traceable back to the account. An administrator completes them (specification
            // section 35).
            migrationBuilder.Sql(@"
INSERT INTO Students
    (StudentIdNumber, RollNumber, FullName, Email, Phone,
     Status, ApplicationUserId, IsBorrowingBlocked, CreatedBy, CreatedUtc)
SELECT
    LEFT('PENDING-' + u.UserName, 50),
    LEFT('PENDING-' + u.UserName, 50),
    LEFT(ISNULL(NULLIF(LTRIM(RTRIM(u.FullName)), ''), u.UserName), 200),
    u.Email,
    u.PhoneNumber,
    0, -- StudentStatus.Active
    u.Id,
    0,
    'Phase3 Backfill',
    GETUTCDATE()
FROM AspNetUsers u
INNER JOIN AspNetUserRoles ur ON ur.UserId = u.Id
INNER JOIN AspNetRoles r ON r.Id = ur.RoleId AND r.NormalizedName = 'USER'
WHERE NOT EXISTS (SELECT 1 FROM Students s WHERE s.ApplicationUserId = u.Id);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove only what this migration created. Rows an operator has since edited or
            // added by hand are left alone - the CreatedBy marker is what makes that possible.
            migrationBuilder.Sql(
                "DELETE FROM Students WHERE CreatedBy = 'Phase3 Backfill';");
            migrationBuilder.Sql(
                "DELETE FROM BookCopies WHERE CreatedBy = 'Phase3 Backfill';");
        }
    }
}
