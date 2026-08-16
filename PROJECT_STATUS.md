# SMA LMS — Project Status

Living document (section 88). Updated at the end of every phase.

**Last updated:** 2026-08-15 — Phase 3 stage 1 complete

---

## Completed

### Pre-phase stabilisation
The inherited application could not start or authenticate. Fixed before the audit could run:

- Created the missing `appsettings.json` (absent from the repository; app could not start)
- Fixed `/Login/Index` 500 — rendered a `_Login` partial that does not exist in the source
- Repointed `LoginPath` from that broken route to the working Identity page
- Made Telegram OTP optional — previously **no user could log in at all** without a bot token
- Seeded Admin/Librarian/Student demo accounts and a 14-book sample catalog

### Phase 1 — Repository audit ✅
- Full static audit of 26,500 lines across controllers, models, services, data, views, Identity
- Application run and exercised over HTTP; all routes swept for status codes
- Searched for existing RFID/barcode/scanner code — **zero matches**
- Searched for D2184 hardware documentation — **no such reader found publicly**
- Produced `ARCHITECTURE.md`, `RFID_ARCHITECTURE.md`, `DATABASE_ARCHITECTURE.md`, `DEPLOYMENT.md`
- Classified all existing functionality: reuse / refactor / replace / remove / new

### Phase 3 stage 1 — Additive schema ✅

22 new tables, 50 indexes, **zero destructive operations**. Migration
`20260814203411_SmaLms_Phase3_AdditiveSchema`.

- Domain enums, location hierarchy, `Student`/`Department`/`AcademicProgram`
- **`BookCopy`** — the entity that unblocks RFID (ARCHITECTURE.md §3.1)
- RFID: `StudentRfidTag`, `BookRfidTag`, `RfidReader`, `RfidScanEvent`, `RfidTransaction`
- Governance: `LibraryPolicy`, `AuditLog`, `SecurityEvent`, `Notification`, `NotificationTemplate`
- Unique filtered indexes on `Epc WHERE IsActive = 1` — one live tag per EPC, history retained
- Verified: `Up()` contains only `CreateTable`/`CreateIndex`; existing data untouched
  (14 books, 8 categories, 3 users); full route regression sweep green

### Phase 5 (partial) — D2184 protocol layer ✅

Pulled forward because the vendor SDK arrived and it was the project's stated blocker.

- Investigated `D2184B.rar`: protocol PDF (41pp), device manual (25pp), readable `Reader` C# library
- Implemented frame codec, command set, all 28 error codes, real-time inventory parser, stream
  reassembler — `Rfid/D2184/`
- **16 protocol checks pass**, including checksums verified byte-exact against the vendor algorithm
- `RFID_ARCHITECTURE.md` §1 rewritten from "blocked" to the actual protocol

### Phase 3 stages 2–5a — Data migration ✅

| Stage | Result |
| --- | --- |
| 2 | 48 `BookCopy` rows generated from `Book.Quantity`; zero-quantity title correctly got none |
| 3 | `Student` rows backfilled from users in the `User` role |
| 4 | Loan→copy mapping with pre-flight abort. **Verified against synthetic loans in a rolled-back transaction** — 5/5 assertions pass |
| 5a | `UX_BorrowingRecords_OneOpenLoanPerCopy` — double-issue proven blocked, return proven to free the copy |

**Stage 5b deliberately deferred** (`BookCopyId` NOT NULL, drop legacy `BookId`). Both are breaking
changes for the inherited borrowing controller, which still writes title-level loans. Applying them
before Phase 7 migrates that controller would leave the application unable to issue a book.

### Phase 4 — Circulation engine ✅

- `ILibraryPolicyService` + `LoanPolicySnapshot` — 18 policy rows seeded, admin-editable.
  Replaces the hardcoded `DefaultBorrowingDays = 14` / `FinePerLateDay = 1.00m` constants.
- `ICirculationService` — one `IssueBookAsync`/`ReturnBookAsync` path for **both** RFID and manual
  (specification §71, §87), wrapped in explicit database transactions (§73)
- Eligibility accumulates every blocker so the desk sees them all at once, each with
  librarian-readable wording (§48)
- Race on the unique index is caught and reported as "already issued", not as a database error

### Test suite ✅ — first tests in the project's history

`Tests/SMA.Lms.Tests` — **22 passing**. Fine arithmetic (including the §23 worked example, grace
periods, configurable rates), transaction-number format, and the D2184 wire protocol.

### Phase 5 — RFID layer ✅

- `IRfidReaderService` / `IRfidDeviceConnection` — application never touches sockets (§87)
- `D2184TcpConnection` — async, cancellable read loop; failures surface instead of being swallowed
- `D2184ReaderService` — transport → framing → inventory → observations, with health tracking
- `RfidScanProcessor` — duplicate suppression per (reader, EPC) with a sliding window
- `SimulatedRfidReaderService` — same interface as real hardware (§4G, §82)
- **Startup refuses the simulator in Production**

### Phase 9 — Notification outbox ✅

- `INotificationOutbox` — rows enqueued inside the caller's transaction, so a dead SMTP server
  can never roll back a loan (§51, §53, §87)
- Exponential backoff, 5 attempts, then abandoned; one bad address never stops a batch
- `OverdueBackgroundService` — **catch-up based, not tick based**, so an app-pool recycle loses
  nothing (DEPLOYMENT.md §2). Deduplication keys make repeated passes idempotent

### Test suite — 45 passing

Fine arithmetic, D2184 protocol, scan deduplication, simulator scenarios, notification scheduling,
and the production simulator guard.

### Phases 6–8 — UI refactor, partial ⚠

**Done**

- `wwwroot/css/sma-design-system.css` — the full design system (§92): tokens, type scale, spacing,
  cards, buttons, badges, forms, tables, alerts, empty states, skeletons, desk components,
  responsive breakpoints, reduced-motion and print rules
- `_SmaLayout.cshtml` — SMA LMS branding and sidebar navigation (§91, §100, §101)
- **RFID checkout screen** (§46, §96, §97) — the flagship. Large desk-readable result banner,
  student/book panels, eligibility shown *before* the Issue button, loan-period chooser capped by
  policy, manual-fallback link, double-submit protection
- **Reader health screen** (§4H) with a meaningful empty state (§104)
- `RfidDemoSeeder` — accession numbers, simulated tags and demo readers so the desk works
  without hardware

Accessibility (§111): every status carries a glyph **and** a word — `✓ Available`, `✕ Issued`,
`● Online` — never colour alone.

**Verified end to end**: student card EPC + book EPC → issue → `SMA-LIB-2026-000012`, due
28 August. Re-issuing the same copy is refused with both reasons listed. Anonymous access to
`/desk/*` redirects to login.

**Return screen** (§18, §19) — book tag alone identifies the loan and the borrower; the fine is
shown *before* the librarian confirms, never as a surprise afterwards.

**Manual issue** (§20, §99) — student and copy search, then the same `ICirculationService` call.
Only `CirculationMethod` differs from the RFID path.

**Student portal** (§10, §66, §93) — answers section 66's eight questions directly: what I have,
when it is due, what I owe, what happens if I am late. Scoped to the signed-in account with no
route parameter that could address another student (§43).

Verified end to end: RFID issue → portal shows it → return by book tag → fine projection → manual
issue → portal reflects both. Students receive 302 on `/desk/*`.

**Catalogue and book detail** (§11, §94, §95) — card grid, search across title/author/ISBN/category,
category filter, sort, availability filter, server-side paging, lazy-loaded covers.
**Availability is derived from `BookCopy` rows, not the legacy `Book.Quantity` scalar** — the copy
table is now the source of truth. Book detail lists every copy with its own status, location and
return date.

**Admin dashboard and policy editor** (§32, §34, §68, §100) — stock, today's circulation, and
system health, with health surfaced *above* the statistics. Policies are editable in the UI and the
cache is invalidated on save.

Verified: changing `Loans.MaximumLoanDays` from 30 to 14 in the admin screen immediately reduced
the desk's offered loan periods from 7/14/21/30 to 7/14. The policy engine is genuinely wired,
not decorative. (Restored to 30 afterwards.)

**RFID tag assignment** (§36, §37, §4F) — `IRfidTagService` enforces the two rules that matter:
a live EPC belongs to exactly one holder (assignment is *blocked*, never silently reassigned), and
replacement ends the old row rather than deleting it (§87). Verified: a book tag is refused as a
student card, a card already held is refused with the holder named, and after replacement the old
card no longer identifies anyone while the new one does.

**Reservations** (§26) — new `Reservation` entity, separating the hold from `CartItem`, which the
Phase 1 audit found doing double duty. FIFO queue with contiguous re-sequencing, per-student limit,
expiry, and a unique filtered index preventing a student queuing twice for one title. Holds are on
the **title**, not a copy — the copy binds only at fulfilment. `CirculationService.ReturnBookAsync`
now offers a returned copy to the next student in the same transaction, marking it `Reserved` so it
is not issued to a passer-by.

**Global search** (§103) — one box across students, books, copies, loans and RFID tags, grouped by
type. Each category is queried and capped independently so one common term cannot crowd out the
others. Verified against all five types, including transaction references and tag EPCs.

**Reports** (§54) — 8 reports with date filters and CSV export: circulation, most-borrowed,
overdue, fines, active students, lost/damaged stock, RFID activity, manual transactions. All
verified running against live data.

CSV export is RFC 4180 with a **formula-injection guard**: a value beginning `=`, `+`, `-` or `@`
is prefixed with an apostrophe, so a book title like `=cmd|...` cannot execute when a librarian
opens the export. 13 tests cover the escaping rules.

**RFID scan persistence** (§4E, §4I, §55, §83) — `IRfidScanRecorder` closes the gap between the
pipeline and the database: it persists deduplicated scans, resolves the EPC against **active**
tag assignments only, updates reader health and copy last-seen, and raises a `SecurityEvent` for
any unrecognised tag (Critical at a gate reader, Info elsewhere).

**Live scan monitor** (§47) with a Development-only simulator that drives the *real* pipeline —
observation → debounce → persist. Verified: 20 raw reads produced **1** logical scan resolved to
the right copy; an immediate repeat was suppressed inside the 1500 ms window; an unknown tag at
the exit gate resolved as unknown and raised a security event now visible on the admin dashboard.
The RFID activity report populates for the first time.

**Reservation desk** (§26) — holds ready for collection separated from students still queueing,
with expiry sweep and release. Releasing a hold returns the held copy to the shelf rather than
stranding it.

**Inherited view migration — theme bridge approach** (§91, §113)

First, a correction to an earlier estimate: only **17 views** use the inherited layouts, not ~51.
The rest are partials, Identity pages and views that inherit via `_ViewStart`.

Rather than rewrite 17 views at once, `wwwroot/css/sma-theme-bridge.css` maps the SMA tokens onto
Bootstrap 5's own `--bs-*` custom properties and normalises buttons, cards, forms, tables, badges,
alerts, pagination and focus rings. Loaded last in `_Layout` and `_AdminLayout`, it rebrands every
inherited view without touching their markup, and views can then migrate to `sma-` classes
individually while looking consistent throughout.

Verified: `--bs-primary` is now SMA navy `#1b4079` (was Bootstrap blue), radius 10px, buttons
6px/600 weight. All 12 inherited pages carry the bridge and the SMA title. Generic
"Library_Management_system" branding removed from both layouts.

### Per-page CSS normalisation ✅

The 27 per-page stylesheets held their own palettes — seven near-identical navies, nine slates,
five near-whites — which is precisely the "random colours" §113 objects to.

Colours were grouped into token families and rewritten as `var(--sma-*)` references. Originals
backed up before the rewrite.

| | Before | After |
| --- | --- | --- |
| Hardcoded hex literals | 699 | **310** |
| Distinct colours | 262 | **219** |
| Token references | 0 | **389** |

Biggest wins: `AdminLayout.css` 14 → 5 distinct colours, `Navbar.css` 24 → 17, `Contact.css` 3 → 1.

Verified at runtime, not just in source: a probe element resolves `--sma-navy-800` to
`rgb(18,48,92)` and `--sma-line` to `rgb(223,228,236)`, and 90 elements on a single page now share
the one ink token. All 25 routes still return 200 and every stylesheet still serves.

**Remaining 219 distinct colours** are genuine one-offs — illustration fills, gradient stops,
shadow tints and decorative accents that do not belong to a token family. Collapsing those further
needs a designer's eye on each screen, not another mechanical pass.

### Not yet verified visually

The browser pane has not been compositing this session, so all styling verification has been
computed-value and DOM inspection. That is precise for tokens, radii and contrast, but it cannot
catch "this screen now looks wrong". **Walk the screens before sign-off.** Originals are in the
session scratchpad under `css-backup/` if any page needs reverting.

### RFID read counts — FIXED

`RfidScanEvent.ReadCount` was written as 1 at burst start and never revisited, so a 20-read burst
was stored as 1.

The row must still be written the instant a burst starts — circulation reacts immediately and
cannot wait to learn how long a student holds the book near the antenna. But the final count is
only knowable once the burst ends. So counts now accumulate in memory and are written back by
`RfidBurstFlushService` after the tag leaves the field. Updating on each read was rejected: §4D
is explicit that repeated observations must not each become a database transaction.

Verified live — the RFID activity report now reads:

| Reader | Logical scans | Raw reads |
| --- | --- | --- |
| Circulation Desk 01 | 3 | 22 |
| Exit Gate 01 | 2 | 31 |

Raw reads exceed logical scans, as they should. Deduplication is unchanged: still one scan per
burst. 8 regression tests cover the write-back, including single-read bursts (no correction),
unpersisted bursts (dropped), per-reader isolation, and the last-seen timestamp reflecting when
the tag actually left rather than when the flush ran.

**Accepted trade-off:** a read count is stale for up to one flush interval (5s) after a burst
ends. The count is a statistic; the scan itself — which circulation depends on — is written
immediately and is never delayed by this.

---

### Phase 10 — Library assistant ✅

**Deliberately not a language model.** §13 requires a controlled assistant that answers only from
application services and "must not invent book availability or locations". A generative model
would introduce exactly that failure, plus an external dependency §2's hosting constraints rule
out. So it is an intent router over the same read-only services the rest of the app uses — every
number it states is read from the database at the moment of asking.

If an LLM is wanted later, the correct shape is to keep these methods as the tool surface and let
the model choose between them. The answers must still come from here.

Verified live: available titles report real copy counts; a fully-borrowed title offers a
reservation instead; an absent title says so rather than inventing one; account questions answer
only for the signed-in student and prompt anonymous users to sign in.

**One chat entry point.** The floating widget on the public site used to be a separate keyword bot
with its own hardcoded answers, so the same question got two different replies depending on where
it was asked. The widget now posts to `/assistant/ask` and renders the real answer plus its links.
The keyword replies survive only as an offline fallback: if the request fails the widget answers
from them when it recognises the intent, and otherwise says it could not reach the catalogue. It
never surfaces a status code or an exception to a student.

The password-reset walkthrough is the one answer the assistant has no data for, so it is still
built client-side. It now describes the email link flow, and the Reset Password page describes the
same thing, so the two agree. Both previously named a Telegram bot belonging to a different
institution and linked to a misspelt handle that did not resolve.

### Telegram removed; password reset moved to email

Telegram was the second factor at sign-in and the only password reset channel. Both are gone.

Sign-in is now a single factor. That is not a downgrade from what was running: the bot was
disabled, and the disabled path already signed users in on the password alone — it just did so
through a branch that logged a warning and pretended a second factor existed.

Reset is ASP.NET Identity's own token, emailed as a single-use link. The previous flow collected
the new password *before* verifying anything and parked it in server memory until an OTP came
back; the token approach stores nothing, is derived from the security stamp, and stops working the
moment the password changes. Verified live: link resets the password, the new password signs in,
replaying the same link is refused with "expired or already been used", and an unregistered
address produces a byte-identical confirmation page so the form cannot be used to discover which
addresses have accounts.

`AccountEmailService` sends both the reset link and the registration/approval alerts that used to
go to a Telegram chat. It is deliberately not routed through `NotificationOutbox`: the outbox is
right for overdue reminders, which can be retried and can arrive late, and wrong for a reset link,
which is worthless if it is late.

Two things this exposed. `ProductionGuards` checked only `EmailSettings:SmtpServer`, and
`appsettings.json` now ships a default (`smtp.gmail.com`) — so the guard would have passed a
deploy with a server name and no credentials, which sends nothing. It now requires server, sender
and password, with a test covering exactly that gap. And migration
`SmaLms_RemoveTelegramOtpColumns` drops the five columns the OTP flow left on `AspNetUsers`;
all five were NULL for every row, checked before applying.

In Development with no SMTP configured nothing is sent and the link is logged at Warning instead,
so the flow stays testable without credentials. It is never shown in the browser — for the few
minutes it lives, that link is equivalent to the password.

### Staff interface — one shell for admin and the desk

The admin pages and the circulation desk had become two different products. Each had its own
layout: `_AdminLayout` with a Bootstrap topbar, its own sidebar and ten per-page stylesheets, and
`_SmaLayout` with design-system styling but no account menu, no logout and no notifications.

The split was not only cosmetic. **Neither menu was complete.** From the admin pages there was no
link to the circulation desk or any RFID screen; from the desk there was no link to books,
students, feedback — or a way to sign out. Whichever side staff were on, part of the product was
unreachable.

Both are replaced by `_StaffLayout` plus a shared `_StaffNav`, listing every destination once
under Overview / Circulation / Catalogue / RFID / People / Insights. The nav hides only what the
signed-in role genuinely cannot open, which was verified rather than assumed: signed in as the
librarian, all fifteen offered links return 200 and none of the four admin-only destinations
appear.

Ten stylesheets totalling 129 KB became one 34 KB `sma-admin.css`. The originals hardcoded **82
distinct hex colours and twelve different border-radius values** between them, which is why no two
admin pages matched. Every value in the replacement is a design token.

The consolidation deliberately keeps the existing class names. `wwwroot/js/admin/*.js` binds to
them — `#sidebarToggle`, `#topbarActions`, `#notificationBadge`, `.contact-item`,
`.edit-category-btn` and dozens more — so restyling the established DOM preserves every modal,
tab, filter and AJAX badge, where renaming across ~3,100 lines of views would have broken them
silently. Where ten files had invented ten names for the same control (eight different "edit this
row" buttons, six different tables), those names are now grouped onto one rule each.

`_AdminLayout` also injected `ApplicationDbContext` and ran six queries inline in the .cshtml.
That moved to a `StaffShell` view component, so the layout holds no data access.

Two defects found by measuring the rendered result rather than trusting the code:

- The topbar rendered at 120 px because `.sma h1` in the design system (specificity 0,1,1) outranks
  a plain class, so the title came out at 2.25 rem. The sidebar's `top: 64px` was therefore wrong
  and it slid under the topbar on scroll. Both now derive from one `--staff-topbar-h` token.
- `sma-admin.css` had been restating `.modal-content`, `.alert` and pagination colours that
  `sma-theme-bridge.css` already owns — and the bridge loads last, so those rules silently lost.
  Removed, with the ownership boundary written down.

Verified across all 21 staff screens: every page renders on the shell, exactly one `<h1>` each (no
page repeats the title the topbar shows), no reference to a deleted stylesheet, and on a fresh
load of the heaviest page — Manage Users, 711 lines — the console is clean and every request is
200. Sidebar collapse persists through `localStorage`, both topbar dropdowns open, the category
modal and tab switching still work, and the unread-feedback poll still returns 200.

### Phase 11 — QA ✅ (101 tests)

Both carried-over items are now discharged. Reservation queue tests build the state the seeded
data never had — a title with every copy on loan — and cover FIFO ordering, gap closing,
fulfilment, hold expiry passing to the next student, and **student isolation** (one student cannot
cancel another's hold).

**Client-side validation was dead on every account form.** The five Identity pages set
`Layout = null`, so they never inherited the site layout's jQuery reference, but they still
included `_ValidationScriptsPartial` — both plugins threw `jQuery is not defined` on load. Nothing
looked broken, because the same rules are enforced server-side on post; the only symptom was a
console error, which is why it survived this long. jQuery now loads from the partial itself, ahead
of the plugins. Verified on Login, Register, ForgotPassword, ResetPassword and LoginOtp: jQuery
present, validator attached to the form, console clean on a fresh load.

### Phase 12 — Production hardening ✅

`ProductionGuards` refuses to start a Production deploy on unsafe configuration: missing or
default admin password, LocalDB connection string, demo seeders enabled, absent SMTP. Development
is untouched. All problems are reported in one message rather than one per restart.

Security headers applied in middleware: `X-Content-Type-Options`, `X-Frame-Options: DENY`,
`Referrer-Policy`, `Permissions-Policy` — verified present on live responses.

---

### Phase 2 — .NET 10 migration ✅

- SDK **10.0.400** installed (runtime 10.0.11). Not elevated, so it went to
  `%LOCALAPPDATA%\Microsoft\dotnet` — see the build note below.
- `net9.0` → `net10.0` for both the web and test projects
- EF Core, Identity and Diagnostics packages `9.0.x` → `10.0.11`
- Dropped `Microsoft.VisualStudio.Web.CodeGeneration.Design` — scaffolding-only, referenced
  nowhere, and the source of repeated NuGet timeouts
- Fixed `MVC1004` surfaced by the upgrade: `[FromServices] UserManager users` collided with
  `UserManager.Users` and risked incorrect model binding. Moved to constructor injection rather
  than suppressed
- Rewrote the CI workflow, which the Phase 1 audit found targeting **8.0.x** against a `net9.0`
  project and running `dotnet test` with no test project

**Build: 0 errors, 0 warnings. All 109 tests pass on net10.0. All 27 routes 200.**

EF-heavy paths re-verified on the new runtime rather than assumed: RFID issue
(`SMA-LIB-2026-002012`), return with fine calculation, the unique-index double-issue guard,
reservation fulfilment, assistant projections, report aggregation and CSV export, global search.

### Build environment — resolved

SDK **10.0.400** is now installed machine-wide in `C:\Program Files\dotnet`, alongside the
existing 9.0.313. Plain `dotnet` on PATH resolves 10.0.400 and builds the project:

```
dotnet build "Library Management system.csproj"
```

`.claude/launch.json` uses plain `dotnet` again — the temporary absolute path is gone.

The temporary user-local copy at `%LOCALAPPDATA%\Microsoft\dotnet` has been **removed** (770 MB
reclaimed). It was verified first as containing nothing but that install, with no PATH entry, no
`DOTNET_ROOT`, and no process running from it.

Verified after both the machine-wide install and the cleanup: clean rebuild 0 errors / 0 warnings,
109 tests pass, all 27 routes 200 with the app launched through plain `dotnet run`.

**One environment note:** the app runs EF migrations and seeding at startup, so the first request
after a restart can arrive before the app is ready to authenticate. Allow a few seconds after
`dotnet run` before scripting logins against it.

---

## In progress

Nothing.

## Observations worth acting on later

- Transaction numbers embed the identity value, so SQL Server's identity cache leaves gaps after a
  restart (`SMA-LIB-2026-000013` → `SMA-LIB-2026-001012`). Unique and correct, but not sequential.
  If librarians expect contiguous numbering, use a dedicated sequence instead.

---

## Blocked

| # | Blocker | Blocks | Needed to unblock |
| --- | --- | --- | --- |
| 1 | ~~.NET 10 SDK not installed~~ | — | ✅ **RESOLVED** — SDK 10.0.400 installed user-local; migration complete |
| 2 | **MyASP.NET runtime version unknown** | Deployment | ⚠ Still unanswered. The app now REQUIRES a .NET 10 runtime. Confirm the host offers one before deploying |
| 3 | ~~D2184 protocol undocumented~~ | — | ✅ **RESOLVED** — vendor SDK supplied, protocol implemented and verified |
| 4 | **RFID network topology undecided** | Live reader connection | Reader is a TCP server on a LAN address; a cloud-hosted app cannot reach it. Local agent (option A) expected. See `DEPLOYMENT.md` §3 |

Blocker 4 does **not** block development: the protocol layer and simulator are testable without it.

---

## Known issues (inherited, not yet fixed)

| Severity | Issue |
| --- | --- |
| High | `Book` and physical copy are one entity — blocks all RFID work (`ARCHITECTURE.md` §3.1) |
| High | Fine rate and loan period are hardcoded consts in a controller |
| High | No service layer; issue/return logic lives inline in a 997-line controller |
| High | Zero automated tests |
| Medium | Password reset still requires Telegram; unusable without a bot token |
| Medium | CI workflow targets .NET 8 against a .NET 9 project and tests nothing |
| Medium | No audit logging anywhere |
| Medium | Student data isolation unverified by any test |
| Low | Duplicate views (`History`, `Profile`); stray `NewFolder`, `TextFile.txt`, `schama/Table.tex` |
| Low | `appsettings.json` holds development seed passwords — must not ship |

---

## Technical debt register

| Item | Origin | Plan |
| --- | --- | --- |
| Business logic in controllers | Inherited | Phase 4 extracts to services |
| `CartItem` doubles as cart and reservation | Inherited | Phase 3 separates |
| `Username` string instead of FK on loans | Inherited | Phase 3 replaces with `StudentId` |
| `DbHelper` ad-hoc data access | Inherited | Phase 4 removes |
| No `AsNoTracking` on read paths | Inherited | Phase 4 |
| ~~Telegram OTP bypass when disabled~~ | Added, then **resolved** | Discharged: the bot is gone, so there is no bypass left to gate. Sign-in is password-only by design and reset moved to email |
| Demo seeders | **Added this session** | Config-gated; Phase 12 verifies off in production |

The demo seeders row is a deliberate, documented trade-off made to get a working system, not an
accident. It is config-gated and must be revisited before production.

---

## Next actions

1. **Decision required from you** on blockers 1 and 2 before Phase 2 (see report).
2. Phase 3 can begin immediately regardless — it is the largest and riskiest phase, and it is
   independent of the .NET version.
3. Phase 3 order: additive tables → copy backfill → student backfill → loan mapping → enforcement.
   Stage 4 gets reviewed against real data before it runs.

---

## Phase ledger

| Phase | Status |
| --- | --- |
| 1 — Repository audit | ✅ Complete |
| 2 — .NET 10 migration | ⛔ Blocked |
| 3 — Database refactor | ▶ Ready |
| 4 — Circulation engine | ⏳ Pending |
| 5 — RFID layer + simulator | ⏳ Pending |
| 6 — Student portal | ⏳ Pending |
| 7 — Librarian portal | ⏳ Pending |
| 8 — Admin portal | ⏳ Pending |
| 9 — Notifications | ⏳ Pending |
| 10 — Library assistant | ⏳ Pending |
| 11 — QA | ⏳ Pending |
| 12 — Production hardening | ⏳ Pending |
