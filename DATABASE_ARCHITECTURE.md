# SMA LMS — Database Architecture

**Phase 1 deliverable.** Target schema, and the data-preserving migration strategy from the
existing 27-migration baseline.

---

## 1. The central change: Book vs BookCopy

Today `Book` carries `Quantity` and `Availability`, and `BorrowingRecord.BookId` points at the
title. There is no physical-item entity.

```
BEFORE                          AFTER
Book                            Book                 (bibliographic title)
  Quantity = 3                    Isbn, Title, Authors, Publisher, Edition...
  Availability = true           BookCopy             (one physical item)
                                  BookId, CopyNumber, Status, ShelfPositionId
BorrowingRecord                   RowVersion
  BookId ──→ Book               BorrowingTransaction
                                  BookCopyId ──→ BookCopy
```

Availability stops being a stored boolean on the title and becomes a derived count of copies whose
status is `Available`. Keeping a denormalised counter is possible later for performance, but only
with the copy table as the source of truth.

This is what makes RFID possible: a tag maps to a `BookCopy`, never to a `Book`.

---

## 2. Core entities

### Catalog
`Book` · `BookCopy` · `Author` · `BookAuthor` · `Category` · `Publisher`

`Book` gains: Subtitle, Publisher, Edition, PublicationYear, Subject, Language (section 8).
`BookCopy` gains: CopyNumber, AcquisitionDate, AcquisitionCost, Status, Condition, RowVersion.

Copy status (section 8): `Available` · `Issued` · `Reserved` · `Lost` · `Damaged` ·
`UnderMaintenance` · `Missing` · `Archived` · `InTransit`

### People
`ApplicationUser` (auth) · `Student` (identity/academic) · `Department` · `Program`

`Student` is deliberately **separate** from `ApplicationUser`. Auth identity and university identity
have different lifecycles: a student record may exist before a login does (bulk import, section 35),
and must survive account deactivation. `Student` holds RollNumber, StudentIdNumber, DepartmentId,
ProgramId, Semester, Cnic, Phone, PhotoPath, Status.

`Cnic` is sensitive (section 44): masked in all UI by default, never written to logs, full value
readable only by Admin.

### Location hierarchy (section 9)
`Library → Building → Floor → Section → Room → Rack → Shelf → ShelfPosition`

`BookCopy.ShelfPositionId` is nullable — section 9 requires showing the most precise location
available, so a copy known only to section level is representable.

### RFID
`StudentRfidTag` · `BookRfidTag` · `RfidReader` · `RfidScanEvent` · `RfidTransaction`
Detailed in `RFID_ARCHITECTURE.md` section 4.

### Circulation
`BorrowingTransaction` · `Reservation` · `Renewal` · `Fine` · `FinePayment` · `FineWaiver`

`BorrowingTransaction` (sections 41, 42):
TransactionNumber (`SMA-LIB-2026-000001`), StudentId, BookCopyId, IssuedUtc, DueUtc, ReturnedUtc?,
Status, IssueMethod (`Rfid|Manual|System`), ReturnMethod, IssueReaderId?, ReturnReaderId?,
IssuedByUserId, ReturnedByUserId?, **RowVersion**.

### Governance
`LibraryPolicy` · `Notification` · `NotificationTemplate` · `NotificationLog` · `AuditLog` ·
`SecurityEvent` · `Announcement`

`AuditLog` is append-oriented (section 38): no update or delete mapped in EF, revoked at the
database level for the application login.

---

## 3. Concurrency (section 42)

Two students must never issue the same physical copy. Three layers, none of them client-side:

1. `BookCopy.RowVersion` and `BorrowingTransaction.RowVersion` as EF concurrency tokens.
2. A **unique filtered index** guaranteeing at most one open loan per copy:

   ```sql
   CREATE UNIQUE INDEX UX_BorrowingTransaction_ActiveCopy
       ON BorrowingTransaction (BookCopyId)
       WHERE ReturnedUtc IS NULL;
   ```

   This is the real guarantee — it holds even if application logic is wrong.
3. Issue and return wrapped in a single explicit transaction covering the loan row, copy status,
   RFID transaction and audit entry (section 73).

---

## 4. Indexes (section 50)

| Table | Index | Kind |
| --- | --- | --- |
| `StudentRfidTag` | `Epc WHERE IsActive = 1` | Unique filtered |
| `BookRfidTag` | `Epc WHERE IsActive = 1` | Unique filtered |
| `BookCopy` | `(BookId, CopyNumber)` | Unique |
| `BookCopy` | `Status`, `ShelfPositionId` | Non-clustered |
| `Book` | `Isbn`, `Title` | Non-clustered |
| `Student` | `RollNumber`, `StudentIdNumber` | Unique |
| `BorrowingTransaction` | `TransactionNumber` | Unique |
| `BorrowingTransaction` | `(BookCopyId) WHERE ReturnedUtc IS NULL` | Unique filtered |
| `BorrowingTransaction` | `DueUtc`, `Status`, `StudentId` | Non-clustered |
| `RfidScanEvent` | `(ReaderId, ObservedUtc)`, `Epc` | Non-clustered |
| `Reservation` | `(BookId, Status, QueuePosition)` | Non-clustered |
| `Notification` | `(Status, NextAttemptUtc)` | Non-clustered — outbox polling |

---

## 5. Migration strategy — the hard part

The existing database holds real borrowing history. Section 61 forbids destroying it, and section 87
forbids deleting historical borrowing transactions.

### The problem

Splitting `Book` into `Book` + `BookCopy` requires repointing every `BorrowingRecord.BookId` at a
specific copy. **Historical rows never recorded which copy was borrowed** — the information does not
exist and cannot be recovered.

### Proposed approach

Staged, each stage its own migration, verified before the next:

1. **Additive only.** Create `BookCopy`, `Student`, location, RFID, policy, audit and notification
   tables. Change nothing existing. Fully reversible.
2. **Backfill copies.** For each `Book` with `Quantity = N`, generate N `BookCopy` rows numbered
   `001..N`, inheriting the title's current status.
3. **Backfill students.** Create a `Student` row for each `ApplicationUser` in the `User` role,
   linked by user id. Roll numbers unknown at this point are marked for admin completion rather than
   invented.
4. **Map loans.** Add nullable `BookCopyId` to the loan table. For each **open** loan, claim a
   distinct available copy of that title. For each **closed** historical loan, attach a per-title
   synthetic copy (`CopyNumber = 'LEGACY'`, status `Archived`) so history is preserved and clearly
   labelled as reconstructed rather than falsely precise.
5. **Enforce.** Only once every row is mapped: make `BookCopyId` non-nullable, add the unique
   filtered index, and drop the legacy `BookId` column from the loan table.

Stage 4 is the one that needs review against real data before it runs. If any title has more open
loans than `Quantity`, the data is already inconsistent and the migration must stop rather than
silently guess — that check runs first and reports offending titles.

### Rollback

Stages 1–3 are additive and reversible by `dotnet ef database update <previous>`. Stages 4–5 are
destructive (column drop) and require a database backup taken immediately before, per
`DEPLOYMENT.md`. Rollback of stage 5 is restore-from-backup, not a down-migration.

---

## 6. Seeding (section 62)

Development seeds: roles, departments, programs, categories, a location tree, default policies, and
sample catalog with copies and simulated RFID tags.

Production: **no default passwords.** The current `Admin@123` fallback in `Program.cs` must be
removed; startup fails fast if `SeedAdmin:Password` is unset in a production environment rather than
falling back to a known value. The demo-user and sample-data seeders added earlier this session are
already config-gated and must be off in production.
