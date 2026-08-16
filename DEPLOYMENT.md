# SMA LMS — Deployment (MyASP.NET)

**Phase 1 deliverable.** Hosting constraints and their architectural consequences.

---

## 1. Hosting reality

Target: **MyASP.NET**, shared Windows ASP.NET hosting. Deployment is Web Deploy or FTP to an IIS
site. There is no SSH, no Docker, no container runtime, no ability to install Windows services, and
no control over the machine outside the site's own application pool.

Consequences that shaped every architectural decision in Phase 1:

| Constraint | Consequence |
| --- | --- |
| No container/orchestration runtime | Single self-contained ASP.NET Core monolith |
| No Redis | `IMemoryCache` only. In-process cache, no distributed cache assumptions |
| No message broker | Notification **outbox in SQL Server**, polled by a hosted service (section 53) |
| No separate worker process | `BackgroundService` inside the web app (section 52) |
| App pool recycles / idles out | Background work must be **resumable and idempotent**, never rely on continuous uptime |
| Shared hosting SMTP limits | Email must be rate-aware and retried, never blocking a transaction |
| No Node.js in production | Any build-time asset tooling must emit committed static files |

---

## 2. The app-pool recycling problem

Shared IIS recycles application pools on idle timeout and on a schedule. This is the single most
important operational constraint, and it directly threatens sections 24 and 52.

A `BackgroundService` therefore **cannot** be treated as a reliable scheduler. Design rules:

- Every background pass is **catch-up based, not tick based**: on each run, query for all work whose
  due time has passed, rather than assuming the previous tick fired.
- Overdue detection and reminders are driven by `Notification` rows with a `NextAttemptUtc`, so a
  missed window is picked up on the next start rather than lost.
- Notification sends are **idempotent** — a unique key per (student, transaction, notification type,
  scheduled date) prevents duplicate emails after a recycle (section 24).
- **No circulation transaction ever depends on a background job** (section 52). Issue and return
  complete synchronously; only the email is deferred.

If reliable scheduling proves impossible on the chosen plan, the documented fallback is an external
uptime pinger hitting a secured endpoint — not a redesign.

---

## 3. RFID and hosting — an unresolved topology question

This needs deciding before Phase 5's real adapter, and it is a genuine architectural fork.

A D2184 reader sits **physically in the library**, on the university LAN. The application will run
**on MyASP.NET, in a datacentre**. A cloud-hosted app cannot open a TCP connection inward to a
reader on a private LAN, and cannot open a serial port that exists on a desk 1,000 km away.

Three options, none of which can be chosen without knowing the reader's connection model
(see `RFID_ARCHITECTURE.md` section 1, item 4):

| Option | How it works | Cost |
| --- | --- | --- |
| **A. Local agent** | A small Windows app at the circulation desk owns the reader and calls the hosted API over HTTPS | Extra component to install and update, but works with any reader and any hosting |
| **B. Reader dials out** | If the D2184 can be configured as a client posting to a URL, it reports directly to the hosted app | Simplest — **only possible if the hardware supports it** |
| **C. On-premise hosting** | Run SMA LMS on a university server instead of MyASP.NET | Contradicts the stated hosting requirement |

**Option A is the safe default** and is what the abstraction is designed to accommodate: the local
agent implements `IRfidDeviceConnection` on the library side and speaks to the app over HTTP, so the
application layer is unchanged either way.

This does not block Phases 2–4, and the simulator covers Phase 5 development.

---

## 4. Configuration and secrets

Never committed (section 59): SMTP password, connection string, admin seed password, any future
RFID or assistant credentials.

On MyASP.NET these are set through the control panel's application-settings UI, which surfaces as
environment variables and overrides `appsettings.json`. Key names use the double-underscore form:

```
ConnectionStrings__DefaultConnection
EmailSettings__Password
SeedAdmin__Password
```

The `appsettings.json` created during this session contains **development** values — a LocalDB
connection string and demo seed passwords. It must not ship as-is. Production hardening (Phase 12)
removes the defaults and makes startup fail fast when required secrets are absent.

---

## 5. Database migrations in production

`Program.cs` currently calls `MigrateAsync()` on every startup. On shared hosting with recycling
this is risky: two workers can start concurrently and race the migration.

EF Core takes a migration lock (`sp_getapplock`, visible in the startup logs of this app), which
makes it safe in practice, but the Phase 12 recommendation is to **decouple migration from startup**
— run migrations deliberately as a deployment step against a backed-up database, and have the app
verify schema compatibility rather than mutate it.

Required order for every release:

1. Back up the production database. Non-negotiable for the Phase 3 stages 4–5 migrations.
2. Apply migrations.
3. Deploy application files.
4. Smoke-test login, catalog, issue, return.

---

## 6. Not yet verified

The following require access to the actual MyASP.NET plan and cannot be confirmed from here:

- Whether the plan permits long-running `BackgroundService` work at all
- SMTP relay limits and whether outbound port 587 is open
- SQL Server edition, size cap and backup facilities
- Whether SignalR WebSockets are supported (section 2 makes SignalR conditional) — this affects the
  live RFID scan screen in section 47, which falls back to polling if WebSockets are unavailable
- Available .NET runtime version — **critical**, since Phase 2 targets .NET 10 and shared hosts
  often lag current releases

The last point is a real risk to Phase 2: upgrading to .NET 10 is pointless if MyASP.NET only
offers a .NET 8 or 9 runtime. **Confirm the supported runtime with the host before Phase 2 runs.**
