# SMA LMS — RFID Architecture (UHF RFID D2184)

**Phase 1 deliverable.** Design for the RFID layer, and an explicit statement of what could not be
determined about the D2184 hardware.

---

## 1. Hardware — RESOLVED

**Status: the D2184 protocol is known and implemented.** Public search found nothing (the only
"D2184" hit is an unrelated Infineon gate driver), but the vendor SDK was supplied directly as
`D2184B.rar` and contains everything needed.

### What the SDK contained

| Artifact | Value |
| --- | --- |
| `UHF RFID Reader Serial Interface Protocol.pdf` | 41 pages — the authoritative wire protocol, V3.1 |
| `D2184 Manual.pdf` | 25 pages — device setup and network defaults |
| `Reader/` C# project | Plain-source protocol implementation: `MessageTran`, `ReaderMethod`, `Talker`, `ITalker` |
| `UHFDemo/` | Demo app — **4 files vendor-encrypted** (`%TSD-Header-###%`), not needed |

The `Reader` library being readable is what mattered: it is the vendor's own implementation, so the
frame format and command codes below are transcribed from working code, not inferred.

### The protocol

**Family:** UHF RFID Reader Serial Interface Protocol **V3.1** — a widely used command set, not
D2184-proprietary. **Transports: both TCP/IP and RS-232 serial**, confirmed in `ReaderMethod`
(`ConnectServer` uses `TcpClient`; `OpenCom` uses `SerialPort`).

**Frame layout:**

```
[0]     0xA0        header
[1]     Len         bytes after this one = address + cmd + data + checksum
[2]     Address     reader address
[3]     Cmd         command code
[4..]   Data        parameters, may be empty
[last]  Checksum    ((~sum) + 1) & 0xFF over every preceding byte
```

Total frame length is `Len + 2`; data length is `Len - 3`.

**Device defaults** (`D2184 Manual.pdf` §1):

| Setting | Value |
| --- | --- |
| IP address | `192.168.0.178` |
| Net mask | `255.255.255.0` |
| **TCP port** | **`4001`** |
| Serial baud | `115200` (also supports 38400) |

**Continuous inventory is supported** — this answers section 4D. Command `0x89`
(`cmd_real_time_inventory`) streams tags as they are seen rather than buffering them. With
`Repeat = 0xFF` a cycle is ~30–50 ms for a single tag in field, which suits a circulation desk.

**Command `0x89` is overloaded across three response shapes**, distinguished only by length:

| Data length | Meaning | Payload |
| --- | --- | --- |
| 1 | Failure | `ErrorCode` |
| 7 | End of round | `AntId(1)` + `ReadRate(2)` + `TotalRead(4)` |
| other | Tag report | `FreqAnt(1)` + `PC(2)` + `EPC(N)` + `RSSI(1)` |

`FreqAnt` packs two fields: **high 6 bits = frequency parameter, low 2 bits = antenna ID**.

> **Protocol ambiguity, worth knowing:** a tag report carrying a 3-byte EPC would also have data
> length 7 and be indistinguishable from an end-of-round summary. Real EPCs are 12 bytes (96-bit),
> so this is safe in practice — but the ambiguity is in the wire format itself, not in our code.
> `D2184InventoryParser` documents this and rejects reports too short to hold an EPC rather than
> guessing.

### Answers to section 4E — which tag fields actually exist

Now known rather than assumed. The reader **does** report RSSI and antenna, so both are populated;
`Rssi` and `Antenna` remain nullable on `RfidScanEvent` because the *simulator* and non-inventory
code paths do not always produce them.

**TID is not returned by real-time inventory** — only EPC, PC and RSSI. Reading TID requires a
separate `ReadTag` (`0x81`) against the TID memory bank. The schema keeps `Tid` nullable and it
stays null on the inventory path.

### What is implemented

| File | Contents |
| --- | --- |
| `Rfid/D2184/D2184Protocol.cs` | Command codes, all 28 error codes with librarian-readable text, device defaults |
| `Rfid/D2184/D2184Frame.cs` | Frame encode/decode, vendor-exact checksum |
| `Rfid/D2184/D2184InventoryParser.cs` | Real-time inventory: tag reports, end-of-round, failures |
| `Rfid/D2184/D2184FrameReader.cs` | Stream reassembly with resynchronisation |

Verified by 16 checks including **hand-computed checksums matching the vendor algorithm
byte-for-byte** (`A0 03 01 72 EA` for GetFirmwareVersion; `A0 04 01 89 FF D3` for real-time
inventory), split frames, three-frame bursts, leading junk recovery and corrupted-checksum
rejection.

### Still outstanding

The **network topology question in `DEPLOYMENT.md` §3 remains open** and is now the only real RFID
blocker. The reader listens as a TCP *server* on port 4001 (`ReaderMethod.ConnectServer` dials out
to it), which means a MyASP.NET-hosted application **cannot reach it** across the internet on a
private LAN address. The local-agent option (A) is therefore the expected answer, not merely the
safe default.

---

## 2. Layering

```
Controllers / SignalR hub          ← never touch sockets or serial ports
        ↓
IRfidScanProcessor                 ← debounce, dedup, correlate
        ↓
IRfidReaderService                 ← device-agnostic operations
        ↓
IRfidDeviceConnection  +  IRfidProtocol  +  IRfidTagParser
        ↓
   ┌────────────────────┬──────────────────────┐
   │ D2184Connection    │ SimulatorConnection  │
   │ (BLOCKED)          │ (buildable now)      │
   └────────────────────┴──────────────────────┘
```

Section 87 forbids the application depending on raw socket or serial code. Controllers subscribe to
processed scan events; they never open a port.

### Interfaces

| Interface | Responsibility |
| --- | --- |
| `IRfidReaderService` | Connect, disconnect, start/stop inventory, report status, raise scan events |
| `IRfidDeviceConnection` | Transport only — bytes in, bytes out. TCP or serial implementation |
| `IRfidProtocol` | Frame/deframe reader commands and responses. **This is the D2184-specific piece** |
| `IRfidTagParser` | Turn a protocol response into `RfidScanEvent` (EPC, optional TID/RSSI/antenna) |
| `IRfidScanProcessor` | Debounce window, duplicate suppression, entity resolution |
| `IRfidTransactionProcessor` | Hand a validated student+copy pair to `ICirculationService` |

Only `IRfidProtocol` and the concrete `IRfidDeviceConnection` are blocked on hardware docs.
Everything above them is hardware-independent and will be built in Phase 5.

---

## 3. Scan pipeline

```
Reader → raw frame → IRfidProtocol → IRfidTagParser → RfidScanEvent
       → IRfidScanProcessor (debounce + dedup)
       → entity resolution (tag → Student | BookCopy | unknown)
       → validation (ICirculationService.ValidateIssueAsync)
       → SQL transaction
       → result → UI + notification outbox + audit log
```

### Deduplication (sections 17, 4D)

A UHF reader observing `EPC001` fifty times in two seconds is **one** logical scan. The processor
keeps a per-reader, per-EPC last-seen timestamp and suppresses repeats inside a configurable window
(`RfidDuplicateWindowMs`, section 58). Suppressed observations increment a read count on the
existing event rather than creating new rows — section 4D forbids one transaction per RF observation.

Deduplication alone is not sufficient. The issue itself must also be **idempotent**: a scan
correlation ID is carried into `ICirculationService`, and a unique constraint on
(copy, active-status) guarantees the database refuses a second concurrent issue of the same physical
copy even if two scans race (sections 42, 73).

---

## 4. Entities

`RfidTag` is modelled as an assignment record, not a string column, so history is never lost
(sections 6, 36, 87):

| Entity | Key fields |
| --- | --- |
| `StudentRfidTag` | Epc, Tid?, StudentId, IsActive, AssignedUtc, ReplacedUtc?, LostUtc?, DamagedUtc? |
| `BookRfidTag` | Epc, Tid?, BookCopyId, IsActive, AssignedUtc, ReplacedUtc? |
| `RfidReader` | Name, Model, Transport, Host?, Port?, ComPort?, Baud?, Purpose, LocationId, IsEnabled, Status, LastHeartbeatUtc, LastScanUtc, LastError |
| `RfidScanEvent` | ReaderId, Epc, Tid?, Rssi?, Antenna?, ObservedUtc, ReadCount, ResolvedEntityType, ResolvedEntityId, CorrelationId |
| `RfidTransaction` | ScanEventId, BorrowingTransactionId?, Operation, ValidationResult, FailureReason?, OperatorUserId? |

Replacement never deletes: the old row gets `ReplacedUtc` and `IsActive = false`, and a new row is
inserted. A unique filtered index on `Epc WHERE IsActive = 1` prevents one tag being live against
two entities (sections 6, 36, 37).

`Rssi` and `Antenna` are nullable precisely because the D2184 may not report them.

---

## 5. Reader status model

`ONLINE` · `OFFLINE` · `CONNECTING` · `ERROR` · `DISABLED`

Health tracking per section 4H: last heartbeat, last successful communication, last tag read,
consecutive failures, reconnect attempts, last error message. A background health check polls
enabled readers on `ReaderHeartbeatInterval` and updates status. Reader unavailability must never
block manual circulation (sections 3, 51).

---

## 6. Simulator (section 4G, section 82)

The simulator implements `IRfidReaderService` directly — the application cannot distinguish it from
hardware, which is the point. It is the only way to build and test Phases 5–11 while the protocol
question is open, and it remains valuable afterwards for automated tests.

Scenarios it must produce: student scan, book scan, multiple books, duplicate burst, unknown EPC,
reader disconnect, reconnect, timeout, invalid tag, blocked student, already-issued copy, on-time
return, overdue return, and synthetic RSSI/antenna values.

Selected by configuration (`Rfid:Provider = Simulator | D2184`) and **must not be registrable in
production** — the composition root will refuse the simulator when the environment is Production.

---

## 7. Security gate and inventory

Both are designed as abstractions now and implemented once hardware is known (sections 28, 29).
Gate readers raise a `SecurityEvent` when a copy with no active loan is observed at an exit reader.
Section 28 explicitly says not to implement hardware-specific alarm control until the gate protocol
is known — so `ISecurityAlarm` will have a logging implementation only.

---

## 8. What Phase 5 will deliver

Buildable without any hardware information:

- All interfaces in section 2 above
- Scan pipeline with debounce and idempotent issue
- All entities and migrations
- Reader management UI and health monitoring
- Full simulator with every scenario in section 6
- Integration tests covering scenarios 1–10 of specification section 64

Deferred until the questions in section 1 are answered:

- `D2184Protocol : IRfidProtocol`
- `D2184TcpConnection` / `D2184SerialConnection : IRfidDeviceConnection`
- Antenna and RSSI persistence, if the reader reports them
- Continuous-inventory start/stop commands
