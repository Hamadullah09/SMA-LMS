# SMA Library Management System — Architecture Assessment

**Phase 1 deliverable.** Audit of the existing `Library-Management-system` codebase and the
proposed target architecture for SMA LMS.

Audit date: 2026-08-15
Audited tree: local copy, `Library-Management-system-master`
Source repo: https://github.com/Rattnakvisal/Library-Management-system/

---

## 1. Audit method and caveat

The local copy is an extracted archive, **not a git clone** — there is no `.git` directory, so
commit history, branch structure and blame were unavailable. The instruction to "inspect the
complete Git history where useful" could not be carried out. Everything below comes from static
inspection of the working tree plus running the application and exercising it over HTTP.

To recover history, re-clone and re-run this audit:

```bash
git clone https://github.com/Rattnakvisal/Library-Management-system.git
```

---

## 2. What the existing system actually is

| Aspect | Finding |
| --- | --- |
| Framework | ASP.NET Core 9 (`net9.0`), MVC + Razor Pages side by side |
| Data | EF Core 9, SQL Server, 27 migrations |
| Identity | ASP.NET Core Identity, `ApplicationUser`, roles Admin/Librarian/User |
| Second factor | Telegram OTP — **the only** OTP channel |
| Email | MailKit SMTP, `EmailSender` |
| UI | Razor views, Bootstrap 5, hand-written JS |
| Tests | **None.** No test project of any kind |
| CI | `.github/workflows/dotnet.yml` — targets `8.0.x`, project is `net9.0`, runs `dotnet test` with no tests |

Code volume:

| Area | Files | Lines |
| --- | --- | --- |
| Controllers | 22 | 5,690 |
| Views | 51 | 6,760 |
| Data (mostly migrations) | 42 | 10,349 |
| Areas/Identity | 24 | 2,144 |
| Models | 29 | 922 |
| Services | 4 | 680 |

The shape is the headline finding: **922 lines of model against 5,690 lines of controller.**
Business logic lives in controllers, not in a domain or service layer.

---

## 3. Blocking findings

These three must be resolved before RFID work can begin. They are not cosmetic.

### 3.1 Book and physical copy are the same entity — BLOCKS RFID

`Models/Book.cs` carries `Quantity` and `Availability` as scalars on the *title*.
`Models/BorrowingRecord.cs` has `BookId → Book`. There is no `BookCopy`.

A UHF RFID tag identifies **one physical item**. The current schema cannot express
"Clean Code copy 002, tag EPC E280...,shelf A3" — so it cannot record which copy a tag denotes,
which copy a student took, or where that copy sits. Section 40 of the specification calls this out,
and the audit confirms it is absent.

Every RFID workflow, the inventory mode, the security gate and per-copy location all depend on
fixing this first. It is the single largest piece of Phase 3.

### 3.2 Business policy is hardcoded in controllers

`Controllers/Admin/ManageBorrowingBookController.cs`:

```csharp
private const int DefaultBorrowingDays = 14;
private const decimal FinePerLateDay = 1.00m;
```

Requirements call for a configurable policy engine (section 22), PKR (section 23), and a 30-day
maximum loan. Currency, rate and loan period are all compile-time constants today, in a controller.

### 3.3 No service layer — issue/return logic cannot be shared

Direct `_context.` usage inside controllers:

| Controller | `_context.` calls | Lines |
| --- | --- | --- |
| `HomeController` | 54 | 1,242 |
| `ManageBorrowingBookController` | 36 | 997 |
| `ManageUserController` | 20 | 939 |

Section 71 requires RFID and manual workflows to call one `ICirculationService`. There is currently
no service to call — the logic exists only inline in `ManageBorrowingBookController`. Building the
RFID path against today's code would necessarily duplicate the business rules, which section 87
explicitly forbids.

---

## 4. Functionality classification

### Reuse as-is

| Component | Note |
| --- | --- |
| ASP.NET Core Identity wiring | Roles, password rules, lockout all sound |
| `EmailSender` (MailKit) | Correct SMTP implementation; needs retry/outbox wrapper, not a rewrite |
| EF Core + SQL Server provider setup | `EnableRetryOnFailure` already configured |
| Migration history | 27 migrations apply cleanly; keep as the baseline to build forward from |
| Static assets (`wwwroot`) | Images, fonts and vendor CSS are reusable |
| Startup role/admin seeding | Correct pattern, extend rather than replace |

### Reuse after refactoring

| Component | Required change |
| --- | --- |
| `Book` model | Split into `Book` (title) + `BookCopy` (physical item). Blocking — see 3.1 |
| `BorrowingRecord` | Add transaction number, copy FK, issue/return method, reader FK, concurrency token. `Username` string becomes a real FK |
| `Fine` | Add rate, waived/paid/outstanding, status enum, payment records. Rate must come from policy |
| `CartItem` | Currently doubles as both cart and reservation. Separate the two concerns |
| Catalog/search in `HomeController` | Query logic is decent; extract to `IBookService`, add projections and indexes |
| Admin CRUD controllers | Keep the routes and views; move logic behind services |
| Report endpoints | Keep queries, move to `IReportingService`, add CSV export |
| Razor views | Keep as structural reference; full visual refactor per sections 90–113 |

### Replace

| Component | Reason |
| --- | --- |
| ~~Telegram-only OTP login~~ **Done** | Sole auth channel with a hard external dependency. Removed entirely: sign-in is password-only, and password reset is an emailed single-use Identity token over SMTP. See `AccountEmailService` and migration `SmaLms_RemoveTelegramOtpColumns` |
| Fine calculation | Hardcoded constants → policy engine |
| Inline issue/return logic | → `ICirculationService` |
| `DbHelper` | Ad-hoc data access; superseded by the service layer |
| CI workflow | Targets wrong SDK, tests nothing |

### Remove

| Component | Reason |
| --- | --- |
| `Views/Login/Index.cshtml` | Already removed this session — rendered a partial that does not exist |
| `Views/User/History.cshtml` vs `Views/User/History/History.cshtml` | Duplicate views |
| `Views/User/Profile/Profile.cshtml` vs `index.cshtml` | Duplicate views |
| `Views/NewFolder`, `wwwroot/images/NewFolder` | Excluded in `.csproj` but still on disk |
| `Views/Shared/TextFile.txt` | Stray file |
| `schama/Table.tex` | Orphaned LaTeX, not a schema source of truth |

### New functionality required

Everything RFID (sections 4A–4I, 5, 6, 16, 17, 28, 29, 82), plus: `Student` entity distinct from
`ApplicationUser`, location hierarchy, policy engine, audit log, notification outbox, security
events, renewal, inventory mode, library assistant, health page, and the entire test suite.

---

## 5. Security findings

| Severity | Finding |
| --- | --- |
| High | `appsettings.json` was **absent from the repository**; the app could not start without it. Seed admin password defaults to `Admin@123` in code (`Program.cs`) |
| High | No test coverage on any authorization boundary |
| Medium | Student data isolation is unverified — no test proves one student cannot read another's records (section 43) |
| Medium | `AccessDeniedPath = "/"` silently redirects home instead of explaining the denial |
| Medium | No audit log; section 38 requires append-oriented auditing of every circulation and RFID event |
| Low | No rate limiting on login |
| Low | No CNIC field exists yet — masking requirement (section 44) applies to Phase 3 onward |

Fixed during this session: missing `appsettings.json`, broken `LoginPath` 500, and login being
impossible without a Telegram bot token.

---

## 6. Performance findings

- `HomeController` is 1,242 lines with 54 direct context calls; several list endpoints materialise
  entities then filter in memory.
- No `AsNoTracking()` on read-only catalog queries.
- Indexes exist only where migrations added them incidentally. Section 50's index list
  (RFID, ISBN, roll number, transaction number, due date, status) is largely unmet.
- Catalog pagination does work — verified, 8 per page.

---

## 7. Target architecture

A single ASP.NET Core monolith, deployable to MyASP.NET as a plain Windows site. No Docker, no
Redis, no external services — consistent with section 2.

```
SMA.Lms.Web            MVC controllers, Razor views, view models, design system
SMA.Lms.Application    Services + interfaces (circulation, policy, catalog, reporting)
SMA.Lms.Domain         Entities, value objects, domain rules, enums
SMA.Lms.Infrastructure EF Core, Identity, SMTP, background jobs, RFID adapters
SMA.Lms.Rfid           Reader abstraction, scan pipeline, D2184 adapter, simulator
SMA.Lms.Tests          Unit, integration, security, concurrency
```

Whether these become separate projects or enforced folders inside one project is a Phase 2
decision — MyASP.NET hosts the compiled output either way, so the choice is about maintainability,
not deployability.

Key rule from section 71, carried through every phase:

```
RFID checkout  ─┐
                ├─→ ICirculationService.IssueBookAsync(...) ─→ SQL transaction ─→ audit + outbox
Manual issue   ─┘
```

One code path. Two entry points.

---

## 8. Migration plan

| Phase | Work | Risk |
| --- | --- | --- |
| 2 | .NET 10 + EF Core 10 upgrade | Blocked — SDK not installed |
| 3 | `BookCopy` split, `Student`, locations, RFID entities | **Highest.** Data-preserving migration; existing `BorrowingRecord.BookId` rows must be mapped to generated copies |
| 4 | Circulation engine + policy engine | Medium — replaces live logic |
| 5 | RFID layer + simulator | Low — new code, no existing behaviour to break |
| 6–8 | UI refactor | Medium — large surface, low logical risk |
| 9–12 | Notifications, assistant, QA, hardening | Low |

### The Phase 3 data-preservation problem

Splitting `Book` into `Book` + `BookCopy` is not a schema-only change. For each existing book with
`Quantity = N`, the migration must generate N `BookCopy` rows, then repoint every historical
`BorrowingRecord.BookId` at a specific copy. Historical rows do not record *which* copy was
borrowed, because that information was never captured.

Proposed approach: generate copies per title, assign active loans to distinct copies
deterministically, and attach closed historical loans to a synthetic "legacy copy" per title so no
borrowing history is deleted (section 87). This is documented in `DATABASE_ARCHITECTURE.md` and must
be reviewed before it runs against real data.

---

## 9. Open blockers

1. **.NET 10 SDK is not installed** — only `9.0.313` is present. Phase 2 cannot start until it is.
2. **D2184 protocol is undocumented** — see `RFID_ARCHITECTURE.md` section "What is needed from you".

Neither blocks Phases 3–4, which proceed on .NET 9 and are forward-compatible.
